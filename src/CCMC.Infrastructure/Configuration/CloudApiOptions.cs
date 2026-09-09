namespace CCMC.Infrastructure.Configuration;

/// <summary>
/// The Windows app's own configuration for reaching the cloud API. The
/// routes HttpCloudApiClient calls are fixed/verified (context.md), but the
/// base URL is environment-specific (dev vs. prod cloud) and must never be
/// hard-coded - see CCMC.Desktop's app configuration file.
/// </summary>
public sealed class CloudApiOptions
{
    /// <summary>e.g. https://ccmc-api.example.com/ - must end with a trailing slash for relative-URI requests to resolve correctly.</summary>
    public required Uri BaseUrl { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
