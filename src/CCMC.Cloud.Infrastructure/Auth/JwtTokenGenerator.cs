using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CCMC.Cloud.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CCMC.Cloud.Infrastructure.Auth;

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues the JWT the Windows client receives from POST /auth/login and
/// sends back as a Bearer token on every subsequent request. Only carries
/// the subject (user id) and email as claims - roles/permissions/centre
/// access are NOT embedded in the token and are instead resolved fresh from
/// the database on every authenticated request (see
/// CurrentUserContextMiddleware/AuthorizationHandlers), so a permission or
/// role change takes effect on the user's very next request instead of only
/// at next login - mirrors the same reasoning the Windows client's own
/// documented contract expectations assume (RequestUser is loaded fresh per
/// request, not cached in the token).
/// </summary>
public interface IJwtTokenGenerator
{
    IssuedToken GenerateToken(User user);
}

public sealed class JwtTokenGenerator(IOptions<JwtOptions> options) : IJwtTokenGenerator
{
    public IssuedToken GenerateToken(User user)
    {
        var jwt = options.Value;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(jwt.ExpiryMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
