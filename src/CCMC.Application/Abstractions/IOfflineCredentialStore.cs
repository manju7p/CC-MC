namespace CCMC.Application.Abstractions;

/// <summary>
/// The minimum identity/role/centre snapshot needed to operate offline,
/// cached from the AuthenticatedUserDto returned by the last successful
/// ONLINE login. Never includes the password or the access token - see
/// IOfflineCredentialStore's doc comment.
/// </summary>
public sealed record OfflineIdentitySnapshot(
    int UserId,
    string Email,
    string FullName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool AllCentres,
    IReadOnlyList<int> CentreIds);

/// <summary>
/// Caches a strong password verifier (never the plaintext password, never
/// the cloud access token) plus an identity/role/centre snapshot, so the
/// operator can sign in and use local-first reception when the cloud is
/// unreachable (BRD v2 "chilling centre must continue operating when
/// Internet connectivity is unavailable"; see CLAUDE.md "Architecture
/// Decisions" for the full design and its explicit constraints:
/// Argon2id verifier, DPAPI-at-rest protection, no token-as-password-substitute).
///
/// SaveAsync is called by AuthenticationService immediately after a
/// successful ONLINE login (the only place the plaintext password is ever
/// available) and overwrites any previous cached credential for that email -
/// so the cached snapshot is always refreshed to the latest online login's
/// roles/permissions/centre access, per the "cloud remains authoritative"
/// requirement.
/// </summary>
public interface IOfflineCredentialStore
{
    Task SaveAsync(string email, string password, OfflineIdentitySnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Returns the cached snapshot if the password verifies against the cached credential for this email; null on any mismatch or if none is cached.</summary>
    Task<OfflineIdentitySnapshot?> VerifyAsync(string email, string password, CancellationToken cancellationToken);
}
