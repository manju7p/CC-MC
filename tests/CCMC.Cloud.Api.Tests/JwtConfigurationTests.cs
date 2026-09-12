using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CCMC.Cloud.Api.Tests.Infrastructure;
using CCMC.Contracts.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CCMC.Cloud.Api.Tests;

/// <summary>
/// Regression test for a real bug found via Docker container testing: a
/// deployment that supplies ONLY Jwt__Secret (a very plausible real mistake -
/// see the Docker verification writeup in STATUS.md) issued tokens with an
/// empty aud/iss claim (JwtTokenGenerator's IOptions&lt;JwtOptions&gt; had no
/// default for Issuer/Audience) while Program.cs's token-validation setup
/// already had its own separate fallback default - so every authenticated
/// request failed with 401 "The audience 'empty' is invalid" the moment
/// Jwt:Issuer/Jwt:Audience were absent from configuration, which is exactly
/// the case for a minimal container `docker run -e Jwt__Secret=...` with no
/// appsettings.Development.json present (excluded from the image on
/// purpose - see .dockerignore). This factory deliberately configures ONLY
/// Jwt:Secret, mirroring that exact real deployment shape, not the local
/// dev appsettings.Development.json (which already had explicit Issuer/
/// Audience values and therefore could never have caught this).
/// </summary>
public sealed class SecretOnlyJwtApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Sources.Clear() first: an explicit null in AddInMemoryCollection does
            // NOT reliably erase a value appsettings.Development.json already set
            // (confirmed directly - the naive "override with null" approach still
            // produced an empty aud/iss claim in a first attempt at this test).
            // Clearing every source and providing only these three keys is what
            // actually and fully matches a real `docker run -e Jwt__Secret=...`
            // deployment with no appsettings.Development.json present at all
            // (excluded from the image on purpose - see .dockerignore).
            config.Sources.Clear();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CcmcDb"] = "Host=localhost;Port=5432;Database=ccmc_cloud_test;Username=postgres;Password=postgres",
                ["Jwt:Secret"] = "docker-repro-test-secret-not-a-real-secret-0123456789-abcdefgh",
                // Jwt:Issuer / Jwt:Audience deliberately NOT set here at all.
            });
        });
    }

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

/// <summary>
/// Tagged into the same collection as every other cloud integration test
/// (not because it shares CcmcApiFactory - it deliberately uses its own
/// SecretOnlyJwtApiFactory - but so xUnit never runs it in parallel with the
/// rest: both factories run EF Core migrations against the same shared
/// ccmc_cloud_test database, and running two independent Migrate() calls
/// concurrently against it races on Postgres DDL (observed directly: a
/// duplicate-key error on pg_type before this attribute was added).
/// </summary>
[Collection(ApiCollection.Name)]
public class JwtConfigurationTests : IClassFixture<SecretOnlyJwtApiFactory>
{
    private readonly SecretOnlyJwtApiFactory _factory;

    public JwtConfigurationTests(SecretOnlyJwtApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_WithOnlyJwtSecretConfigured_IssuesTokenWithNonEmptyAudienceAndIssuer()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("auth/login", new LoginRequestDto { Email = "operator1@ccmc.local", Password = "Operator@12345" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);

        var payload = DecodeJwtPayload(body!.AccessToken);
        Assert.Contains("\"aud\":\"ccmc-windows-client\"", payload);
        Assert.Contains("\"iss\":\"ccmc-cloud-api\"", payload);
    }

    [Fact]
    public async Task AuthenticatedEndpoint_WithOnlyJwtSecretConfigured_Succeeds()
    {
        // This is the exact failure this bug produced: a valid login followed by
        // a 401 on the very next authenticated call, because the issued token's
        // audience/issuer didn't match what the validator required.
        var client = await _factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");

        var response = await client.GetAsync("centres");

        var wwwAuth = response.Headers.WwwAuthenticate.ToString();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected OK, got {response.StatusCode}. WWW-Authenticate: {wwwAuth}");
    }

    private static string DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
    }
}
