using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Application.MasterData;

public sealed record UpsertRateFormulaSettingsCommand(
    int? CentreId, RateFormulaType RateType, decimal? Value1, decimal? Value2, decimal? TsRate);

/// <summary>
/// BRD v5.0 section 25 (PREFS_RATE_*) - stored, configurable data, never
/// hard-coded (same requirement QualityRuleService already follows). Unlike
/// QualityRuleService, this is an upsert-by-centre (not update-by-id):
/// nothing is seeded with fabricated Value1/Value2/TsRate numbers (the BRD
/// gives no example values, unlike section 10's FAT/SNF/Temperature limits),
/// so there is no existing row to PATCH the first time a centre configures
/// its rate formula.
/// </summary>
public sealed class RateFormulaSettingsService(CcmcDbContext db)
{
    public Task<List<RateFormulaSettings>> ListAsync(CancellationToken cancellationToken) =>
        db.RateFormulaSettings.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<RateFormulaSettings> UpsertAsync(UpsertRateFormulaSettingsCommand cmd, CancellationToken cancellationToken)
    {
        var existing = await db.RateFormulaSettings.SingleOrDefaultAsync(s => s.CentreId == cmd.CentreId, cancellationToken);

        if (existing is null)
        {
            existing = new RateFormulaSettings { CentreId = cmd.CentreId };
            db.RateFormulaSettings.Add(existing);
        }

        existing.RateType = cmd.RateType;
        existing.Value1 = cmd.Value1;
        existing.Value2 = cmd.Value2;
        existing.TsRate = cmd.TsRate;

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }
}
