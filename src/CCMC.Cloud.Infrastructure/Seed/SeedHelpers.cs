using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Auth;
using CCMC.Cloud.Infrastructure.Persistence;
using CCMC.Contracts.Auth;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Infrastructure.Seed;

/// <summary>
/// Idempotent upsert helpers shared by DevelopmentSeeder and
/// ProductionBootstrapSeeder - split out once a genuine second caller
/// (the production bootstrap) needed the exact same role/permission grants
/// and upsert semantics, so the two seeders cannot drift apart on RBAC.
/// </summary>
internal static class SeedHelpers
{
    /// <summary>
    /// Least-privilege Operator/Manager/Admin permission grants (BRD v2
    /// section 14's RBAC requirement) - the single source of truth for both
    /// seeders.
    /// </summary>
    public static readonly Dictionary<string, string[]> RolePermissions = new()
    {
        ["Operator"] =
        [
            PermissionCodes.SourceView, PermissionCodes.VehicleView, PermissionCodes.QualityRuleView,
            PermissionCodes.RateFormulaView,
            PermissionCodes.ReceptionView, PermissionCodes.ReceptionCreate, PermissionCodes.DashboardView,
        ],
        ["Manager"] =
        [
            PermissionCodes.SourceView, PermissionCodes.SourceCreate, PermissionCodes.SourceEdit,
            PermissionCodes.VehicleView, PermissionCodes.VehicleCreate, PermissionCodes.VehicleEdit,
            PermissionCodes.QualityRuleView, PermissionCodes.QualityRuleConfigure,
            PermissionCodes.RateFormulaView, PermissionCodes.RateFormulaConfigure,
            PermissionCodes.ReceptionView, PermissionCodes.ReceptionCreate, PermissionCodes.ReceptionOverride,
            PermissionCodes.DashboardView, PermissionCodes.AuditView,
        ],
        ["Admin"] =
        [
            PermissionCodes.SourceView, PermissionCodes.SourceCreate, PermissionCodes.SourceEdit,
            PermissionCodes.VehicleView, PermissionCodes.VehicleCreate, PermissionCodes.VehicleEdit,
            PermissionCodes.QualityRuleView, PermissionCodes.QualityRuleConfigure,
            PermissionCodes.RateFormulaView, PermissionCodes.RateFormulaConfigure,
            PermissionCodes.ReceptionView, PermissionCodes.ReceptionCreate, PermissionCodes.ReceptionOverride,
            PermissionCodes.DashboardView, PermissionCodes.AuditView,
        ],
    };

    public static async Task SeedRolesAndPermissionsAsync(CcmcDbContext db)
    {
        var allCodes = RolePermissions.Values.SelectMany(v => v).Distinct().ToList();
        foreach (var code in allCodes)
        {
            if (await db.Permissions.AnyAsync(p => p.Code == code)) continue;
            var parts = code.Split('_', 2);
            db.Permissions.Add(new Permission { Code = code, Module = parts[0], Action = parts.Length > 1 ? parts[1] : code });
        }
        await db.SaveChangesAsync();

        foreach (var (roleName, codes) in RolePermissions)
        {
            var role = await db.Roles.Include(r => r.RolePermissions).SingleOrDefaultAsync(r => r.Name == roleName);
            if (role is null)
            {
                role = new Role { Name = roleName, IsSystemDefault = true };
                db.Roles.Add(role);
                await db.SaveChangesAsync();
            }

            foreach (var code in codes)
            {
                var permission = await db.Permissions.SingleAsync(p => p.Code == code);
                var exists = await db.RolePermissions.AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == permission.Id);
                if (!exists)
                {
                    db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
                }
            }
        }
        await db.SaveChangesAsync();
    }

    public static async Task<ChillingCentre> UpsertCentreAsync(CcmcDbContext db, string code, string name)
    {
        var centre = await db.ChillingCentres.SingleOrDefaultAsync(c => c.Code == code);
        if (centre is not null) return centre;

        centre = new ChillingCentre { Code = code, Name = name, Status = RecordStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.ChillingCentres.Add(centre);
        await db.SaveChangesAsync();
        return centre;
    }

    /// <summary>
    /// Creates the user if the email doesn't already exist. Never overwrites
    /// PasswordHash on an existing user - a bootstrap/seed run must never
    /// silently reset a real production password.
    /// </summary>
    public static async Task<(User User, bool Created)> UpsertUserAsync(CcmcDbContext db, IPasswordHasher hasher, string email, string fullName, string password)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (user is not null) return (user, false);

        user = new User
        {
            Email = email, FullName = fullName, PasswordHash = hasher.Hash(password), Status = RecordStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user, true);
    }

    public static async Task AssignRoleAsync(CcmcDbContext db, int userId, int roleId)
    {
        var exists = await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId);
        if (!exists) db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
    }

    public static async Task AssignCentreAsync(CcmcDbContext db, int userId, int? centreId, bool allCentres)
    {
        var exists = await db.UserCentreAssignments.AnyAsync(a => a.UserId == userId && a.CentreId == centreId);
        if (!exists) db.UserCentreAssignments.Add(new UserCentreAssignment { UserId = userId, CentreId = centreId, AllCentres = allCentres });
    }

    public static async Task UpsertGlobalRuleAsync(CcmcDbContext db, QualityParameter parameter, decimal min, decimal max)
    {
        var exists = await db.QualityRules.AnyAsync(r => r.Parameter == parameter && r.CentreId == null);
        if (!exists) db.QualityRules.Add(new QualityRule { Parameter = parameter, MinValue = min, MaxValue = max, CentreId = null });
    }
}
