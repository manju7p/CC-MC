using CCMC.Cloud.Application.Audit;
using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Auth;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CCMC.Cloud.Application.Auth;

public sealed record LoginOutcome(bool Success, string? AccessToken, DateTimeOffset? ExpiresAt, RequestUser? User);

/// <summary>
/// Authenticates against POST /auth/login's contract (verified against the
/// Windows client's own CCMC.Contracts.Auth.LoginRequestDto/LoginResponseDto -
/// see CLAUDE.md "API Contract"). Timing-safe against email enumeration: a
/// password check always runs, even for an unknown email, against a fixed
/// dummy hash, so "no such user" cannot be distinguished from "wrong
/// password" by response timing alone.
/// </summary>
public sealed class AuthenticationService(
    CcmcDbContext db,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator tokenGenerator,
    UserContextService userContext,
    IAuditService audit,
    ILogger<AuthenticationService> logger)
{
    // Computed once, independent of DI - a fixed timing-safety placeholder,
    // never a real credential, never persisted or compared against anything meaningful.
    private static readonly string DummyPasswordHash = new PasswordHasherAdapter().Hash("no-such-user-timing-safety-placeholder");

    public async Task<LoginOutcome> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

        var passwordMatches = passwordHasher.Verify(user?.PasswordHash ?? DummyPasswordHash, password);

        if (user is null || user.Status != RecordStatus.Active || !passwordMatches)
        {
            logger.LogWarning("Login failed for {Email}.", email);
            return new LoginOutcome(false, null, null, null);
        }

        var context = await userContext.LoadAsync(user.Id, cancellationToken);
        if (context is null)
        {
            logger.LogWarning("Login failed for {Email} (user context could not be loaded).", email);
            return new LoginOutcome(false, null, null, null);
        }

        var issued = tokenGenerator.GenerateToken(user);

        await audit.RecordAsync(new AuditEntry(user.Id, null, "LOGIN", "User", user.Id.ToString()), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Login succeeded for {Email} (user {UserId}).", email, user.Id);
        return new LoginOutcome(true, issued.AccessToken, issued.ExpiresAt, context);
    }
}
