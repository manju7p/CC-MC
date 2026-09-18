using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Application.Common;
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

    /// <summary>
    /// Centre-scoped, same enforcement pattern as SourceService/VehicleService's own
    /// CentreAccessGuard.AssertCanAccess calls (2026-09-18 correction pass - this endpoint
    /// previously had no centre check at all, meaning any Manager/Admin with
    /// RATE_FORMULA_CONFIGURE could silently overwrite ANY centre's configuration, or the
    /// shared global default, regardless of their own centre assignment - the exact "one
    /// centre's configuration overwriting another's" outcome this session's Centre Scoping
    /// requirement explicitly forbids). A null <paramref name="cmd"/>.CentreId (the global
    /// default row, applied to every centre that has no centre-specific override - see
    /// RateFormulaSettings' own doc comment) may only be written by a caller whose
    /// CentreAccess.AllCentres is true, since that row is not scoped to any single centre a
    /// non-AllCentres Manager could be said to "own".
    /// </summary>
    public async Task<RateFormulaSettings> UpsertAsync(RequestUser user, UpsertRateFormulaSettingsCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.CentreId is { } centreId)
        {
            CentreAccessGuard.AssertCanAccess(user.CentreAccess, centreId);

            var centreExists = await db.ChillingCentres.AnyAsync(c => c.Id == centreId, cancellationToken);
            if (!centreExists) throw new ValidationException("Centre not found.");
        }
        else if (!user.CentreAccess.AllCentres)
        {
            throw new CentreAccessDeniedException(centreId: null);
        }

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
