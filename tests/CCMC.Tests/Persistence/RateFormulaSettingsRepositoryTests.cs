using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Infrastructure.Persistence.Repositories;
using Xunit;

namespace CCMC.Tests.Persistence;

public class RateFormulaSettingsRepositoryTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public RateFormulaSettingsRepositoryTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReplaceAllAsync_ThenListAsync_RoundTripsAllFields()
    {
        var repo = new RateFormulaSettingsRepository(_fixture.ConnectionFactory);

        await repo.ReplaceAllAsync(
            [new RateFormulaSettings { Id = 1, RateType = RateFormulaType.FatVsSnf, Value1 = 10m, Value2 = 8m, TsRate = null, CentreId = null }],
            CancellationToken.None);

        var all = await repo.ListAsync(CancellationToken.None);
        var setting = Assert.Single(all);
        Assert.Equal(RateFormulaType.FatVsSnf, setting.RateType);
        Assert.Equal(10m, setting.Value1);
        Assert.Equal(8m, setting.Value2);
        Assert.Null(setting.TsRate);
        Assert.Null(setting.CentreId);
    }

    [Fact]
    public async Task ResolveForCentreAsync_CentreSpecificOverridesGlobal()
    {
        var repo = new RateFormulaSettingsRepository(_fixture.ConnectionFactory);

        await repo.ReplaceAllAsync(
            [
                new RateFormulaSettings { Id = 1, RateType = RateFormulaType.TsBased, TsRate = 5m, CentreId = null }, // global
                new RateFormulaSettings { Id = 2, RateType = RateFormulaType.FatVsSnf, Value1 = 10m, Value2 = 8m, CentreId = 1 }, // centre 1 override
            ],
            CancellationToken.None);

        var forCentre1 = await repo.ResolveForCentreAsync(1, CancellationToken.None);
        Assert.Equal(RateFormulaType.FatVsSnf, forCentre1!.RateType);

        var forCentre2 = await repo.ResolveForCentreAsync(2, CancellationToken.None);
        Assert.Equal(RateFormulaType.TsBased, forCentre2!.RateType); // falls back to global
    }

    [Fact]
    public async Task ResolveForCentreAsync_NothingConfigured_ReturnsNull()
    {
        var repo = new RateFormulaSettingsRepository(_fixture.ConnectionFactory);
        await repo.ReplaceAllAsync([], CancellationToken.None);

        var result = await repo.ResolveForCentreAsync(1, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReplaceAllAsync_CalledAgain_ClearsPreviousSettings()
    {
        var repo = new RateFormulaSettingsRepository(_fixture.ConnectionFactory);
        await repo.ReplaceAllAsync(
            [new RateFormulaSettings { Id = 1, RateType = RateFormulaType.TsBased, TsRate = 5m, CentreId = null }],
            CancellationToken.None);

        await repo.ReplaceAllAsync(
            [new RateFormulaSettings { Id = 2, RateType = RateFormulaType.FatVsSnf, Value1 = 1m, Value2 = 1m, CentreId = null }],
            CancellationToken.None);

        var all = await repo.ListAsync(CancellationToken.None);
        var setting = Assert.Single(all);
        Assert.Equal(RateFormulaType.FatVsSnf, setting.RateType);
    }
}
