using System.Net.Http.Headers;
using System.Net.Http.Json;
using CCMC.Contracts.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CCMC.Cloud.Api.Tests.Infrastructure;

/// <summary>
/// Boots the real API (real Program.cs startup path - real migrations, real
/// Development-only seed) against a SEPARATE database (ccmc_cloud_test, not
/// the developer's own ccmc_cloud_dev) so integration tests exercise the
/// genuine startup/migration/seed/auth/authorization pipeline end to end,
/// not a special test-only shortcut. Requires a local PostgreSQL reachable
/// exactly as README.md describes - these are integration tests, not unit
/// tests, and are skipped implicitly (they fail fast with a clear connection
/// error) if no database is available.
/// </summary>
public sealed class CcmcApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CcmcDb"] = "Host=localhost;Port=5432;Database=ccmc_cloud_test;Username=postgres;Password=postgres",
            });
        });
    }

    /// <summary>Logs in as one of the seeded development users and returns a client with the Bearer token already attached.</summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(string email, string password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("auth/login", new LoginRequestDto { Email = email, Password = password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CcmcApiFactory>
{
    public const string Name = "Api";
}
