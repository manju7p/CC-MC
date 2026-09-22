namespace CCMC.Cloud.Infrastructure.Auth;

/// <summary>
/// Bound from configuration (Jwt:Secret / Jwt:Issuer / Jwt:Audience /
/// Jwt:ExpiryMinutes) - never hard-coded. See appsettings.Development.json's
/// placeholder + README for how a real deployment must override Jwt:Secret
/// via environment variable / user-secrets, never commit a real one.
///
/// Issuer/Audience DO have defaults here (unlike Secret, which must never
/// have a silent fallback) - discovered via real Docker container testing:
/// Program.cs's token-VALIDATION setup already falls back to
/// "ccmc-cloud-api"/"ccmc-windows-client" when Jwt:Issuer/Jwt:Audience are
/// absent from configuration, but this options class (bound via
/// IOptions&lt;JwtOptions&gt; and used by JwtTokenGenerator to actually ISSUE
/// tokens) had no matching default - so a deployment that supplies only
/// Jwt__Secret (a very plausible real mistake) issued tokens with an empty
/// aud/iss claim while the validator still required the fallback values,
/// making every authenticated request fail with 401 "The audience 'empty'
/// is invalid". These defaults must stay identical to Program.cs's own
/// fallback strings so the two sides can never disagree.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string DefaultIssuer = "ccmc-cloud-api";
    public const string DefaultAudience = "ccmc-windows-client";

    public required string Secret { get; init; }
    public string Issuer { get; init; } = DefaultIssuer;
    public string Audience { get; init; } = DefaultAudience;
    public int ExpiryMinutes { get; init; } = 480; // 8h, matching the BRD's illustrative session length
}
