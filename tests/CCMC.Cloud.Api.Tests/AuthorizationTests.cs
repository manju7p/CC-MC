using System.Net;
using System.Net.Http.Json;
using CCMC.Cloud.Api.Tests.Infrastructure;
using CCMC.Contracts.Dtos;
using Xunit;

namespace CCMC.Cloud.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthorizationTests(CcmcApiFactory factory)
{
    [Fact]
    public async Task Admin_HasAllCentresAccess_CanSeeBothSeededCentres()
    {
        var admin = await factory.CreateAuthenticatedClientAsync("admin@ccmc.local", "Admin@12345");
        var centres = await admin.GetFromJsonAsync<List<ChillingCentreDto>>("centres");

        Assert.NotNull(centres);
        Assert.True(centres!.Count >= 2); // BLR-CC-01 and MYS-CC-01 at minimum
    }

    [Fact]
    public async Task Operator_CentreScoped_OnlySeesOwnCentre()
    {
        var operator1 = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345"); // Bangalore
        var centres = await operator1.GetFromJsonAsync<List<ChillingCentreDto>>("centres");

        Assert.NotNull(centres);
        Assert.Single(centres!);
        Assert.Equal("BLR-CC-01", centres![0].Code);
    }

    [Fact]
    public async Task Operator_LacksReceptionOverridePermission_Returns403()
    {
        var operator1 = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");
        var response = await operator1.PostAsJsonAsync(
            "reception/999999/override", new OverrideReceptionRequestDto { NewStatus = CCMC.Contracts.Enums.TransactionStatus.ACCEPTED, Reason = "test" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Manager_HasReceptionOverridePermission_NotForbidden()
    {
        // Uses a nonexistent transaction id - expect 404 (reached the handler,
        // permission check passed), NOT 403 (which would mean the permission
        // check itself failed).
        var manager = await factory.CreateAuthenticatedClientAsync("manager1@ccmc.local", "Manager@12345");
        var response = await manager.PostAsJsonAsync(
            "reception/999999/override", new OverrideReceptionRequestDto { NewStatus = CCMC.Contracts.Enums.TransactionStatus.ACCEPTED, Reason = "test" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Operator_FromMysore_CannotAccessBangaloreCentreSources()
    {
        var operator2 = await factory.CreateAuthenticatedClientAsync("operator2@ccmc.local", "Operator@12345"); // Mysore
        var sources = await operator2.GetFromJsonAsync<List<SourceDto>>("sources");

        Assert.NotNull(sources);
        Assert.Contains(sources!, s => s.Code == "SRC-MYS-001"); // this operator's own centre's source IS visible
        Assert.DoesNotContain(sources!, s => s.Code == "SRC-BLR-001"); // the other centre's source must NOT leak across the boundary
    }
}
