using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Auth;
using CCMC.Cloud.Infrastructure.Persistence;
using CCMC.Contracts.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CCMC.Cloud.Infrastructure.Seed;

/// <summary>
/// DEVELOPMENT-ONLY seed data - run automatically at startup, but only when
/// ASPNETCORE_ENVIRONMENT=Development (see Program.cs). Never runs against a
/// production configuration. Credentials below are placeholder development
/// passwords already documented in this repository's own README.md
/// ("Development Credentials") from this same engineering effort - reused
/// here for consistency with what a developer following the README expects,
/// NOT copied from the old pranav-dev implementation (which was never
/// consulted for this file). Role→permission grants and quality-rule limits
/// are this seeder's own fresh judgment calls, informed directly by BRD v2
/// (section 10's own FAT/SNF/Temperature limits table, and section 14's
/// least-privilege/RBAC requirement) - not copied from any prior codebase.
/// </summary>
public static class DevelopmentSeeder
{
    private static readonly Dictionary<string, string[]> RolePermissions = new()
    {
        // Least-privilege operator: can capture/view receptions and reference data for their centre.
        ["Operator"] =
        [
            PermissionCodes.SourceView, PermissionCodes.VehicleView, PermissionCodes.QualityRuleView,
            PermissionCodes.RateFormulaView,
            PermissionCodes.ReceptionView, PermissionCodes.ReceptionCreate, PermissionCodes.DashboardView,
        ],
        // Manager: additionally configures master data/quality rules/rate formula, resolves HOLDs, views audit.
        ["Manager"] =
        [
            PermissionCodes.SourceView, PermissionCodes.SourceCreate, PermissionCodes.SourceEdit,
            PermissionCodes.VehicleView, PermissionCodes.VehicleCreate, PermissionCodes.VehicleEdit,
            PermissionCodes.QualityRuleView, PermissionCodes.QualityRuleConfigure,
            PermissionCodes.RateFormulaView, PermissionCodes.RateFormulaConfigure,
            PermissionCodes.ReceptionView, PermissionCodes.ReceptionCreate, PermissionCodes.ReceptionOverride,
            PermissionCodes.DashboardView, PermissionCodes.AuditView,
        ],
        // Admin: every permission that exists.
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

    public static async Task SeedAsync(CcmcDbContext db, IPasswordHasher passwordHasher, ILogger logger)
    {
        // --- Permissions -----------------------------------------------------
        var allCodes = RolePermissions.Values.SelectMany(v => v).Distinct().ToList();
        foreach (var code in allCodes)
        {
            if (await db.Permissions.AnyAsync(p => p.Code == code)) continue;
            var parts = code.Split('_', 2);
            db.Permissions.Add(new Permission { Code = code, Module = parts[0], Action = parts.Length > 1 ? parts[1] : code });
        }
        await db.SaveChangesAsync();

        // --- Roles + role-permission mapping ----------------------------------
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

        // --- Centres -----------------------------------------------------------
        var centreBlr = await UpsertCentreAsync(db, "BLR-CC-01", "Chilling Centre - Bangalore");
        var centreMys = await UpsertCentreAsync(db, "MYS-CC-01", "Chilling Centre - Mysore");

        // --- Users -------------------------------------------------------------
        var adminRole = await db.Roles.SingleAsync(r => r.Name == "Admin");
        var managerRole = await db.Roles.SingleAsync(r => r.Name == "Manager");
        var operatorRole = await db.Roles.SingleAsync(r => r.Name == "Operator");

        var admin = await UpsertUserAsync(db, passwordHasher, "admin@ccmc.local", "System Administrator", "Admin@12345");
        var manager1 = await UpsertUserAsync(db, passwordHasher, "manager1@ccmc.local", "Bangalore Manager", "Manager@12345");
        var operator1 = await UpsertUserAsync(db, passwordHasher, "operator1@ccmc.local", "Bangalore Operator", "Operator@12345");
        var operator2 = await UpsertUserAsync(db, passwordHasher, "operator2@ccmc.local", "Mysore Operator", "Operator@12345");

        await AssignRoleAsync(db, admin.Id, adminRole.Id);
        await AssignRoleAsync(db, manager1.Id, managerRole.Id);
        await AssignRoleAsync(db, operator1.Id, operatorRole.Id);
        await AssignRoleAsync(db, operator2.Id, operatorRole.Id);

        await AssignCentreAsync(db, admin.Id, centreId: null, allCentres: true);
        await AssignCentreAsync(db, manager1.Id, centreBlr.Id, allCentres: false);
        await AssignCentreAsync(db, operator1.Id, centreBlr.Id, allCentres: false);
        await AssignCentreAsync(db, operator2.Id, centreMys.Id, allCentres: false);

        // --- Global quality rules - values taken directly from BRD v2 section 10 ----
        await UpsertGlobalRuleAsync(db, QualityParameter.Fat, 3.0m, 6.0m);
        await UpsertGlobalRuleAsync(db, QualityParameter.Snf, 8.0m, 10.0m);
        await UpsertGlobalRuleAsync(db, QualityParameter.Temperature, 0m, 10.0m);

        // --- Minimal sources/vehicles so reception is immediately exercisable ----
        await UpsertSourceAsync(db, "SRC-BLR-001", "Collection Centre A", centreBlr.Id);
        await UpsertSourceAsync(db, "SRC-MYS-001", "Collection Centre B", centreMys.Id);
        await UpsertVehicleAsync(db, "KA01AB1234", "Ramesh", centreBlr.Id);
        await UpsertVehicleAsync(db, "KA09CD5678", "Suresh", centreMys.Id);

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Development seed complete. Seeded users: admin@ccmc.local/Admin@12345 (Admin, all centres), " +
            "manager1@ccmc.local/Manager@12345 (Manager, {BlrCentreCode1}), operator1@ccmc.local/Operator@12345 (Operator, {BlrCentreCode2}), " +
            "operator2@ccmc.local/Operator@12345 (Operator, {MysCentreCode}). DEVELOPMENT ONLY - never use in production.",
            centreBlr.Code, centreBlr.Code, centreMys.Code);
    }

    private static async Task<ChillingCentre> UpsertCentreAsync(CcmcDbContext db, string code, string name)
    {
        var centre = await db.ChillingCentres.SingleOrDefaultAsync(c => c.Code == code);
        if (centre is not null) return centre;

        centre = new ChillingCentre { Code = code, Name = name, Status = RecordStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.ChillingCentres.Add(centre);
        await db.SaveChangesAsync();
        return centre;
    }

    private static async Task<User> UpsertUserAsync(CcmcDbContext db, IPasswordHasher hasher, string email, string fullName, string password)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (user is not null) return user;

        user = new User
        {
            Email = email, FullName = fullName, PasswordHash = hasher.Hash(password), Status = RecordStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task AssignRoleAsync(CcmcDbContext db, int userId, int roleId)
    {
        var exists = await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId);
        if (!exists) db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
    }

    private static async Task AssignCentreAsync(CcmcDbContext db, int userId, int? centreId, bool allCentres)
    {
        var exists = await db.UserCentreAssignments.AnyAsync(a => a.UserId == userId && a.CentreId == centreId);
        if (!exists) db.UserCentreAssignments.Add(new UserCentreAssignment { UserId = userId, CentreId = centreId, AllCentres = allCentres });
    }

    private static async Task UpsertGlobalRuleAsync(CcmcDbContext db, QualityParameter parameter, decimal min, decimal max)
    {
        var exists = await db.QualityRules.AnyAsync(r => r.Parameter == parameter && r.CentreId == null);
        if (!exists) db.QualityRules.Add(new QualityRule { Parameter = parameter, MinValue = min, MaxValue = max, CentreId = null });
    }

    private static async Task UpsertSourceAsync(CcmcDbContext db, string code, string name, int centreId)
    {
        var exists = await db.Sources.AnyAsync(s => s.Code == code);
        if (!exists)
        {
            db.Sources.Add(new Source
            {
                Code = code, Name = name, MilkType = "Cow", CentreId = centreId, Status = RecordStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private static async Task UpsertVehicleAsync(CcmcDbContext db, string vehicleNumber, string driverName, int centreId)
    {
        var exists = await db.Vehicles.AnyAsync(v => v.VehicleNumber == vehicleNumber);
        if (!exists)
        {
            db.Vehicles.Add(new Vehicle
            {
                VehicleNumber = vehicleNumber, DriverName = driverName, DriverMobile = "9900000000",
                TankerNumber = $"TNK-{vehicleNumber[^4..]}", CapacityKg = 5000m, CentreId = centreId, Status = RecordStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
    }
}
