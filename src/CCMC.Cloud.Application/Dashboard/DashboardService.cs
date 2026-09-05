using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Application.Common;
using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Application.Dashboard;

public sealed record DashboardSummary(string DateLabel, IReadOnlyList<int> CentreIds, int TotalTransactions, int Accepted, int Rejected, int Hold);

/// <summary>
/// "Today" is the IST (UTC+5:30) calendar day, computed with a fixed offset
/// rather than a timezone library - an explicit, documented assumption for
/// this India-based dairy chilling-centre deployment (BRD's stated domain),
/// not a hidden invention. If centres ever operate outside India, this needs
/// to become per-centre configurable.
/// </summary>
public sealed class DashboardService(CcmcDbContext db)
{
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public async Task<DashboardSummary> GetSummaryAsync(RequestUser user, int? centreIdFilter, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> centreIds;
        if (centreIdFilter is { } requested)
        {
            CentreAccessGuard.AssertCanAccess(user.CentreAccess, requested);
            centreIds = [requested];
        }
        else if (user.CentreAccess.AllCentres)
        {
            centreIds = await db.ChillingCentres.AsNoTracking().Select(c => c.Id).ToListAsync(cancellationToken);
        }
        else
        {
            centreIds = user.CentreAccess.CentreIds;
        }

        var nowIst = DateTimeOffset.UtcNow.ToOffset(IstOffset);
        var dayStartIst = new DateTimeOffset(nowIst.Year, nowIst.Month, nowIst.Day, 0, 0, 0, IstOffset);
        var dayEndIst = dayStartIst.AddDays(1);

        // Npgsql only accepts UTC (offset zero) DateTimeOffset values for
        // "timestamp with time zone" comparisons (a real error hit during
        // manual verification: "Cannot write DateTimeOffset with
        // Offset=05:30:00... only offset 0 (UTC) is supported") - the IST
        // offset above is only used to determine which CALENDAR DAY "today"
        // means; the actual query bounds must be converted to UTC.
        var dayStartUtc = dayStartIst.ToUniversalTime();
        var dayEndUtc = dayEndIst.ToUniversalTime();

        var todaysTransactions = await db.MilkReceptionTransactions.AsNoTracking()
            .Where(t => centreIds.Contains(t.CentreId) && t.ReceivedAt >= dayStartUtc && t.ReceivedAt < dayEndUtc)
            .Select(t => t.Status)
            .ToListAsync(cancellationToken);

        return new DashboardSummary(
            dayStartIst.ToString("yyyy-MM-dd"),
            centreIds,
            todaysTransactions.Count,
            todaysTransactions.Count(s => s == TransactionStatus.Accepted),
            todaysTransactions.Count(s => s == TransactionStatus.Rejected),
            todaysTransactions.Count(s => s == TransactionStatus.Hold));
    }
}
