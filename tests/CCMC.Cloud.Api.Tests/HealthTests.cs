using CCMC.Cloud.Api.Tests.Infrastructure;
using Xunit;

namespace CCMC.Cloud.Api.Tests;

[Collection(ApiCollection.Name)]
public class HealthTests(CcmcApiFactory factory)
{
    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("health");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthDb_ReturnsHealthy_ProvingRealDatabaseConnectivity()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("health/db");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
