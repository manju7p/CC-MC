namespace CCMC.Cloud.Infrastructure.Auth;

/// <summary>
/// Bound from configuration (Jwt:Secret / Jwt:Issuer / Jwt:Audience /
/// Jwt:ExpiryMinutes) - never hard-coded. See appsettings.Development.json's
/// placeholder + README for how a real deployment must override Jwt:Secret
/// via environment variable / user-secrets, never commit a real one.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public required string Secret { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public int ExpiryMinutes { get; init; } = 480; // 8h, matching the BRD's illustrative session length
}
