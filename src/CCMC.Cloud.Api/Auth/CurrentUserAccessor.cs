using System.IdentityModel.Tokens.Jwt;
using CCMC.Cloud.Application.Auth;

namespace CCMC.Cloud.Api.Auth;

/// <summary>
/// Resolves the authenticated caller's RequestUser (roles/permissions/centre
/// access) fresh from the database, once per HTTP request (registered
/// Scoped - see Program.cs), from the JWT's "sub" claim. Never trusts
/// anything except the verified JWT subject and the database itself.
/// </summary>
public interface ICurrentUserAccessor
{
    Task<RequestUser> GetRequiredAsync(CancellationToken cancellationToken);
}

public sealed class CurrentUserAccessor(IHttpContextAccessor httpContextAccessor, UserContextService userContext) : ICurrentUserAccessor
{
    private RequestUser? _cached;

    public async Task<RequestUser> GetRequiredAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null) return _cached;

        var subject = httpContextAccessor.HttpContext?.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (subject is null || !int.TryParse(subject, out var userId))
        {
            throw new UnauthorizedAccessException("No authenticated user on the current request.");
        }

        var context = await userContext.LoadAsync(userId, cancellationToken)
            ?? throw new UnauthorizedAccessException("User no longer active.");

        _cached = context;
        return context;
    }
}
