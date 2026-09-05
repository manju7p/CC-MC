using CCMC.Cloud.Application.Common;
using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Application.MasterData;

public sealed record UpdateQualityRuleCommand(decimal MinValue, decimal MaxValue);

/// <summary>
/// Quality rules are stored data, never hard-coded in application code (this
/// session's explicit requirement) - GET /quality-rules is what the Windows
/// client actually calls today (MasterDataSyncService caches the result
/// locally for fully-offline validation). The client does not yet call any
/// update endpoint, but PATCH is provided here anyway so the
/// QUALITY_RULE_CONFIGURE permission has somewhere to apply, per the BRD's
/// "configurable" requirement - a small, additive endpoint, not a client
/// contract change.
/// </summary>
public sealed class QualityRuleService(CcmcDbContext db)
{
    public Task<List<QualityRule>> ListAsync(CancellationToken cancellationToken) =>
        db.QualityRules.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<QualityRule> UpdateAsync(int id, UpdateQualityRuleCommand cmd, CancellationToken cancellationToken)
    {
        var rule = await db.QualityRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Quality rule {id} not found.");

        rule.MinValue = cmd.MinValue;
        rule.MaxValue = cmd.MaxValue;
        await db.SaveChangesAsync(cancellationToken);
        return rule;
    }
}
