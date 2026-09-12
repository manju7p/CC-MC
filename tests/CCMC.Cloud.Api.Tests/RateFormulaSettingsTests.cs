using System.Net;
using System.Net.Http.Json;
using CCMC.Cloud.Api.Controllers;
using CCMC.Cloud.Api.Tests.Infrastructure;
using CCMC.Contracts.Dtos;
using CCMC.Contracts.Enums;
using Xunit;

namespace CCMC.Cloud.Api.Tests;

/// <summary>BRD v5.0 section 25 (PREFS_RATE_*) - GET/PUT /rate-formula-settings, mirroring QualityRulesController's own test coverage pattern.</summary>
[Collection(ApiCollection.Name)]
public class RateFormulaSettingsTests(CcmcApiFactory factory)
{
    [Fact]
    public async Task List_ReturnsOkWithAList()
    {
        // Other tests in this shared-database test collection may add rows here
        // (same convention as AuthorizationTests' centre-count assertions using
        // ">=", not "=="), so this only proves the endpoint round-trips a list -
        // it does NOT assert the table is empty. That "no fabricated default is
        // ever seeded" claim is proven in isolation instead, by
        // ReceptionWorkflowServiceTests.ValidateAndSaveAsync_NoRateFormulaConfigured_RateAndAmountAreZero
        // (a fresh, per-test SQLite database) and by DevelopmentSeeder's own code
        // (no UpsertGlobalRateFormulaSettingsAsync-style call exists for it).
        var client = await factory.CreateAuthenticatedClientAsync("admin@ccmc.local", "Admin@12345");

        var response = await client.GetAsync("rate-formula-settings");
        var settings = await response.Content.ReadFromJsonAsync<List<RateFormulaSettingsDto>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(settings);
    }

    [Fact]
    public async Task Upsert_ByManager_CreatesNewGlobalSettings()
    {
        var manager = await factory.CreateAuthenticatedClientAsync("manager1@ccmc.local", "Manager@12345");
        var request = new RateFormulaSettingsController.UpsertRateFormulaSettingsRequest(
            CentreId: null, RateType: RateFormulaType.FAT_VS_SNF, Value1: 10m, Value2: 8m, TsRate: null);

        var response = await manager.PutAsJsonAsync("rate-formula-settings", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<RateFormulaSettingsDto>();
        Assert.Equal(RateFormulaType.FAT_VS_SNF, dto!.RateType);
        Assert.Equal(10m, dto.Value1);
        Assert.Equal(8m, dto.Value2);
        Assert.Null(dto.CentreId);
    }

    [Fact]
    public async Task Upsert_CalledTwiceForSameCentre_UpdatesRatherThanDuplicating()
    {
        var manager = await factory.CreateAuthenticatedClientAsync("manager1@ccmc.local", "Manager@12345");
        var centreId = (await manager.GetFromJsonAsync<List<ChillingCentreDto>>("centres"))!.Single(c => c.Code == "BLR-CC-01").Id;

        await manager.PutAsJsonAsync("rate-formula-settings",
            new RateFormulaSettingsController.UpsertRateFormulaSettingsRequest(centreId, RateFormulaType.FAT_VS_SNF, 10m, 8m, null));
        var second = await manager.PutAsJsonAsync("rate-formula-settings",
            new RateFormulaSettingsController.UpsertRateFormulaSettingsRequest(centreId, RateFormulaType.TS_BASED, null, null, 5m));

        var dto = await second.Content.ReadFromJsonAsync<RateFormulaSettingsDto>();
        Assert.Equal(RateFormulaType.TS_BASED, dto!.RateType);
        Assert.Equal(5m, dto.TsRate);

        var all = await manager.GetFromJsonAsync<List<RateFormulaSettingsDto>>("rate-formula-settings");
        Assert.Single(all!.Where(s => s.CentreId == centreId)); // still exactly one row for this centre, not two
    }

    [Fact]
    public async Task Upsert_ByOperator_Returns403()
    {
        // Operator has RATE_FORMULA_VIEW but not RATE_FORMULA_CONFIGURE.
        var operator1 = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");
        var request = new RateFormulaSettingsController.UpsertRateFormulaSettingsRequest(null, RateFormulaType.TS_BASED, null, null, 5m);

        var response = await operator1.PutAsJsonAsync("rate-formula-settings", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_ByOperator_Succeeds()
    {
        // Operator has RATE_FORMULA_VIEW - read access, matching QualityRuleView's own grant.
        var operator1 = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");

        var response = await operator1.GetAsync("rate-formula-settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
