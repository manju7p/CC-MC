using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Application.MasterData;

/// <summary>
/// Reference-data: which centres this user can act within. No fine-grained
/// permission required beyond being authenticated - there is no separate
/// "centre management" capability in this slice (mirrors the Windows
/// client's own documented expectation for GET /centres).
/// </summary>
public sealed class CentreService(CcmcDbContext db)
{
    public async Task<IReadOnlyList<ChillingCentre>> ListAsync(RequestUser user, CancellationToken cancellationToken)
    {
        if (user.CentreAccess.AllCentres)
        {
            return await db.ChillingCentres.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);
        }

        if (user.CentreAccess.CentreIds.Count == 0) return [];

        return await db.ChillingCentres.AsNoTracking()
            .Where(c => user.CentreAccess.CentreIds.Contains(c.Id))
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }
}
