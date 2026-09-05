using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Application.Auth;

/// <summary>
/// Loads a user's roles, permissions, and centre access fresh from the
/// database - called on every authenticated request (see RequestUser's doc
/// comment for why this is deliberate, not a missed caching opportunity).
/// </summary>
public sealed class UserContextService(CcmcDbContext db)
{
    public async Task<RequestUser?> LoadAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.Status != RecordStatus.Active) return null;

        var roleNames = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Name)
            .ToListAsync(cancellationToken);

        var permissionCodes = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .Distinct()
            .ToListAsync(cancellationToken);

        var assignments = await db.UserCentreAssignments.AsNoTracking()
            .Where(a => a.UserId == userId)
            .ToListAsync(cancellationToken);

        var allCentres = assignments.Any(a => a.AllCentres);
        var centreIds = assignments.Where(a => !a.AllCentres && a.CentreId is not null).Select(a => a.CentreId!.Value).ToList();

        return new RequestUser(user.Id, user.Email, user.FullName, roleNames, permissionCodes, new CentreAccess(allCentres, centreIds));
    }
}
