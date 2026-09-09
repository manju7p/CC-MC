using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CCMC.Application.Abstractions;
using CCMC.Infrastructure.Persistence;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CCMC.Infrastructure.Auth;

/// <summary>
/// Caches a strong password verifier (Argon2id, never the plaintext password
/// or the cloud access token) plus the identity/role/centre snapshot needed
/// for offline operation, DPAPI-protected at rest under the current Windows
/// user's profile - see IOfflineCredentialStore's doc comment and CLAUDE.md
/// "Architecture Decisions" (Offline Operator Login) for the full design and
/// why each choice was made.
///
/// One row per email in offline_credentials (email TEXT PRIMARY KEY,
/// protected_blob TEXT, updated_at TEXT). The blob, before DPAPI protection,
/// is a small JSON document containing the Argon2id salt+hash and the
/// identity/role/centre snapshot together - protecting both as one unit is
/// simpler to reason about than splitting "secret" from "not secret" fields,
/// and errs toward more protection rather than less.
///
/// [SupportedOSPlatform("windows")]: DPAPI (System.Security.Cryptography.ProtectedData)
/// is Windows-only - honestly declared here rather than suppressed, matching
/// this app's actual target (CCMC.Desktop is net8.0-windows; CCMC.Infrastructure
/// as a whole already assumes Windows via System.IO.Ports for real hardware).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfflineCredentialStore(SqliteConnectionFactory connectionFactory, ILogger<OfflineCredentialStore> logger)
    : IOfflineCredentialStore
{
    // Argon2id cost parameters. This runs once per login attempt (not per
    // request), so a deliberately expensive-but-tolerable cost is used -
    // roughly in line with OWASP's Argon2id guidance for interactive login
    // (>=19 MiB memory, low iteration count) - erring toward more memory
    // since a single operator login is not a hot path.
    private const int SaltLengthBytes = 16;
    private const int HashLengthBytes = 32;
    private const int DegreeOfParallelism = 4;
    private const int Iterations = 4;
    private const int MemorySizeKb = 65536; // 64 MB

    private sealed record StoredCredential(
        string SaltBase64,
        string HashBase64,
        int UserId,
        string Email,
        string FullName,
        List<string> Roles,
        List<string> Permissions,
        bool AllCentres,
        List<int> CentreIds);

    public async Task SaveAsync(string email, string password, OfflineIdentitySnapshot snapshot, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);
        var hash = ComputeHash(password, salt);

        var stored = new StoredCredential(
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash),
            snapshot.UserId,
            snapshot.Email,
            snapshot.FullName,
            snapshot.Roles.ToList(),
            snapshot.Permissions.ToList(),
            snapshot.AllCentres,
            snapshot.CentreIds.ToList());

        var json = JsonSerializer.Serialize(stored);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), optionalEntropy: null, DataProtectionScope.CurrentUser);
        var protectedBase64 = Convert.ToBase64String(protectedBytes);

        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO offline_credentials (email, protected_blob, updated_at)
            VALUES ($email, $blob, $updatedAt)
            ON CONFLICT (email) DO UPDATE SET protected_blob = excluded.protected_blob, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$blob", protectedBase64);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation("Cached/refreshed offline credential for {Email}.", email);
    }

    public async Task<OfflineIdentitySnapshot?> VerifyAsync(string email, string password, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_blob FROM offline_credentials WHERE email = $email;";
        command.Parameters.AddWithValue("$email", email);
        var protectedBase64 = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (protectedBase64 is null)
        {
            logger.LogInformation("No cached offline credential for {Email}.", email);
            return null;
        }

        StoredCredential? stored;
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedBase64);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
            stored = JsonSerializer.Deserialize<StoredCredential>(json);
        }
        catch (Exception ex)
        {
            // Cannot decrypt/parse - e.g. the DPAPI user profile key changed
            // (different Windows account), or the row is corrupted. Fail
            // closed (no offline login) rather than throw out of a login
            // attempt or fall back to any less-safe comparison.
            logger.LogWarning(ex, "Failed to read cached offline credential for {Email} - offline login unavailable for this account.", email);
            return null;
        }

        if (stored is null) return null;

        var salt = Convert.FromBase64String(stored.SaltBase64);
        var expectedHash = Convert.FromBase64String(stored.HashBase64);
        var actualHash = ComputeHash(password, salt);

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            return null;
        }

        return new OfflineIdentitySnapshot(
            stored.UserId, stored.Email, stored.FullName, stored.Roles, stored.Permissions, stored.AllCentres, stored.CentreIds);
    }

    private static byte[] ComputeHash(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = DegreeOfParallelism,
            Iterations = Iterations,
            MemorySize = MemorySizeKb,
        };
        return argon2.GetBytes(HashLengthBytes);
    }
}
