using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Application.Common;
using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Application.Audit;

public sealed class AuditQueryService(CcmcDbContext db)
{
    public async Task<IReadOnlyList<AuditLog>> ListAsync(
        RequestUser user, string? resourceType, int? centreIdFilter, CancellationToken cancellationToken)
    {
        var query = db.AuditLogs.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(200).AsQueryable();

        if (!user.CentreAccess.AllCentres)
        {
            var ids = user.CentreAccess.CentreIds.Count > 0 ? user.CentreAccess.CentreIds : [-1];
            query = query.Where(a => a.CentreId != null && ids.Contains(a.CentreId.Value));
        }

        if (centreIdFilter is { } centreId)
        {
            CentreAccessGuard.AssertCanAccess(user.CentreAccess, centreId);
            query = query.Where(a => a.CentreId == centreId);
        }

        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            query = query.Where(a => a.ResourceType == resourceType);
        }

        return await query.ToListAsync(cancellationToken);
    }
}
