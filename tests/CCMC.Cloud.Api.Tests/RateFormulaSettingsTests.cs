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

    /// <summary>
    /// 2026-09-18 correction pass: manager1 is centre-scoped (BLR-CC-01 only, not
    /// AllCentres - see DevelopmentSeeder), and the global (CentreId: null) default row
    /// applies to every centre with no centre-specific override, including centres this
    /// Manager has no access to. Previously this endpoint had no centre check at all and
    /// this test asserted 200 OK for exactly that case - see RateFormulaSettingsService
    /// .UpsertAsync's own doc comment for why that was the actual "one centre's config
    /// overwriting another's" bug this session's Centre Scoping requirement forbids.
    /// A centre-scoped Manager writing their OWN centre's settings is covered separately
    /// by Upsert_CalledTwiceForSameCentre_UpdatesRatherThanDuplicating below and by
    /// Upsert_ByManager_ForOwnCentre_Succeeds.
    /// </summary>
    [Fact]
    public async Task Upsert_ByCentreScopedManager_ForGlobalDefault_Returns403()
    {
        var manager = await factory.CreateAuthenticatedClientAsync("manager1@ccmc.local", "Manager@12345");
        var request = new RateFormulaSettingsController.UpsertRateFormulaSettingsRequest(
            CentreId: null, RateType: RateFormulaType.FAT_VS_SNF, Value1: 10m, Value2: 8m, TsRate: null);

        var response = await manager.PutAsJsonAsync("rate-formula-settings", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Admin is seeded with AllCentres (see DevelopmentSeeder) - the one role that may write the global default row.</summary>
    [Fact]
    public async Task Upsert_ByAdmin_ForGlobalDefault_Succeeds()
    {
        var admin = await factory.CreateAuthenticatedClientAsync("admin@ccmc.local", "Admin@12345");
        var request = new RateFormulaSettingsController.UpsertRateFormulaSettingsRequest(
            CentreId: null, RateType: RateFormulaType.FAT_VS_SNF, Value1: 11m, Value2: 9m, TsRate: null);

        var response = await admin.PutAsJsonAsync("rate-formula-settings", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<RateFormulaSettingsDto>();
        Assert.Equal(RateFormulaType.FAT_VS_SNF, dto!.RateType);
        Assert.Equal(11m, dto.Value1);
        Assert.Equal(9m, dto.Value2);
        Assert.Null(dto.CentreId);
    }

    /// <summary>A Manager may configure the specific centre they are actually assigned to (BLR-CC-01).</summary>
    [Fact]
    public async Task Upsert_ByManager_ForOwnCentre_Succeeds()
    {
        var manager = await factory.CreateAuthenticatedClientAsync("manager1@ccmc.local", "Manager@12345");
        var centreId = (await manager.GetFromJsonAsync<List<ChillingCentreDto>>("centres"))!.Single(c => c.Code == "BLR-CC-01").Id;
        var request = new RateFormulaSettingsController.UpsertRateFormulaSettingsRequest(
            CentreId: centreId, RateType: RateFormulaType.FAT_VS_SNF, Value1: 10m, Value2: 8m, TsRate: null);

        var response = await manager.PutAsJsonAsync("rate-formula-settings", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<RateFormulaSettingsDto>();
        Assert.Equal(centreId, dto!.CentreId);
    }

    /// <summary>A Manager scoped to BLR-CC-01 must not be able to configure a different centre (MYS-CC-01, operator2's centre) - this is exactly the "one centre overwriting another's configuration" outcome Centre Scoping forbids.</summary>
    [Fact]
    public async Task Upsert_ByManager_ForOtherCentre_Returns403()
    {
        // manager1's own GET /centres only ever returns BLR-CC-01 (CentreService filters
        // to the caller's own access) - MYS-CC-01's id is fetched via an Admin client
        // (AllCentres) instead, purely to discover the id to attempt the PUT with.
        var admin = await factory.CreateAuthenticatedClientAsync("admin@ccmc.local", "Admin@12345");
        var otherCentreId = (await admin.GetFromJsonAsync<List<ChillingCentreDto>>("centres"))!.Single(c => c.Code == "MYS-CC-01").Id;

        var manager = await factory.CreateAuthenticatedClientAsync("manager1@ccmc.local", "Manager@12345");
        var request = new RateFormulaSettingsController.UpsertRateFormulaSettingsRequest(
            CentreId: otherCentreId, RateType: RateFormulaType.FAT_VS_SNF, Value1: 10m, Value2: 8m, TsRate: null);

        var response = await manager.PutAsJsonAsync("rate-formula-settings", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
