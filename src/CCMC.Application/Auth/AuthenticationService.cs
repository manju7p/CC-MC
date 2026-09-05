using CCMC.Application.Abstractions;
using CCMC.Contracts.Auth;
using CCMC.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CCMC.Application.Auth;

public sealed record LoginResult(bool Success, string? ErrorMessage, bool IsOffline = false);

/// <summary>
/// Online-first login, with an offline fallback for when the cloud is
/// genuinely unreachable - see CLAUDE.md "Architecture Decisions" (Offline
/// Operator Login) for the full product decision this implements:
///
/// - Online success: cloud remains authoritative. The just-verified
///   plaintext password (never persisted) is used once, here, to
///   refresh/cache an Argon2id verifier + identity/role/centre snapshot via
///   IOfflineCredentialStore, so a LATER offline login can use it.
/// - Online rejection (bad credentials, disabled account, etc.) - the cloud
///   was actually reached and is authoritative; this is NEVER second-guessed
///   by falling back to a possibly-stale offline cache.
/// - Network failure (cloud unreachable) - falls back to verifying against
///   the cached offline credential, if one exists for this email. An offline
///   session carries no usable access token (see Session.IsOffline) - sync
///   stays paused until the operator successfully authenticates online again.
///
/// Security-category logging here logs email (an identifier, not a secret)
/// and outcome only - the password and the issued access token are NEVER
/// logged, on success or failure.
/// </summary>
public sealed class AuthenticationService(
    ICloudApiClient cloudApiClient,
    ISessionStore sessionStore,
    IOfflineCredentialStore offlineCredentialStore,
    IAuditLogRepository auditLogRepository,
    IClock clock,
    ILogger<AuthenticationService> logger)
{
    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var result = await cloudApiClient.LoginAsync(email, password, cancellationToken);

        if (result.Success && result.Response is not null)
        {
            return await OnOnlineLoginSucceededAsync(email, password, result.Response, cancellationToken);
        }

        if (!result.IsNetworkFailure)
        {
            // Reached the cloud; it authoritatively rejected this login.
            logger.LogWarning("Login rejected by cloud for {Email}.", email);
            return new LoginResult(false, result.ErrorMessage ?? "Login failed.");
        }

        return await TryOfflineLoginAsync(email, password, cancellationToken);
    }

    private async Task<LoginResult> OnOnlineLoginSucceededAsync(
        string email, string password, LoginResponseDto response, CancellationToken cancellationToken)
    {
        var user = response.User;
        sessionStore.Set(new Session(response.AccessToken, user, clock.UtcNow, IsOffline: false));
        logger.LogInformation("Login succeeded (online) for {Email} (user {UserId}).", email, user.Id);

        try
        {
            await offlineCredentialStore.SaveAsync(
                email,
                password,
                new OfflineIdentitySnapshot(
                    user.Id, user.Email, user.FullName, user.Roles, user.Permissions,
                    user.CentreAccess.AllCentres, user.CentreAccess.CentreIds),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Caching the offline credential must never block a successful
            // online login - the operator can still work online; only a later
            // offline login for this account would be affected.
            logger.LogWarning(ex, "Failed to cache offline credentials for {Email} - offline login will be unavailable for this account until the next successful online login.", email);
        }

        await auditLogRepository.RecordAsync(
            new AuditLogEntry
            {
                UserId = user.Id,
                CentreId = null,
                Action = "LOGIN",
                ResourceType = "User",
                ResourceId = user.Id.ToString(),
                CreatedAt = clock.UtcNow,
            },
            cancellationToken);

        return new LoginResult(true, null);
    }

    private async Task<LoginResult> TryOfflineLoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        logger.LogInformation("Cloud unreachable for login - attempting offline credential verification for {Email}.", email);

        var snapshot = await offlineCredentialStore.VerifyAsync(email, password, cancellationToken);
        if (snapshot is null)
        {
            logger.LogWarning("Offline login failed for {Email} (no cached credential, or password did not match).", email);
            return new LoginResult(false, "Cannot reach the cloud, and no valid cached offline credential exists for this account.");
        }

        var offlineUser = new AuthenticatedUserDto
        {
            Id = snapshot.UserId,
            Email = snapshot.Email,
            FullName = snapshot.FullName,
            Roles = snapshot.Roles.ToList(),
            Permissions = snapshot.Permissions.ToList(),
            CentreAccess = new CentreAccessSummaryDto { AllCentres = snapshot.AllCentres, CentreIds = snapshot.CentreIds.ToList() },
        };

        // No usable access token - see Session.IsOffline's doc comment and
        // SyncEngineService, which treats an offline session the same as "no
        // session" for sync purposes.
        sessionStore.Set(new Session(string.Empty, offlineUser, clock.UtcNow, IsOffline: true));
        logger.LogInformation("Login succeeded (OFFLINE) for {Email} (user {UserId}).", email, snapshot.UserId);

        await auditLogRepository.RecordAsync(
            new AuditLogEntry
            {
                UserId = snapshot.UserId,
                CentreId = null,
                Action = "LOGIN_OFFLINE",
                ResourceType = "User",
                ResourceId = snapshot.UserId.ToString(),
                CreatedAt = clock.UtcNow,
            },
            cancellationToken);

        return new LoginResult(true, null, IsOffline: true);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var user = sessionStore.Current?.User;
        sessionStore.Clear();

        if (user is not null)
        {
            logger.LogInformation("Logout for {Email} (user {UserId}).", user.Email, user.Id);

            await auditLogRepository.RecordAsync(
                new AuditLogEntry
                {
                    UserId = user.Id,
                    CentreId = null,
                    Action = "LOGOUT",
                    ResourceType = "User",
                    ResourceId = user.Id.ToString(),
                    CreatedAt = clock.UtcNow,
                },
                cancellationToken);
        }
    }
}
