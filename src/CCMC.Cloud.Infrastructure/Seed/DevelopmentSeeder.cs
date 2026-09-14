using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Auth;
using CCMC.Cloud.Infrastructure.Persistence;
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
/// consulted for this file). Role→permission grants (SeedHelpers.RolePermissions)
/// and quality-rule limits are this seeder's own fresh judgment calls,
/// informed directly by BRD v2 (section 10's own FAT/SNF/Temperature limits
/// table, and section 14's least-privilege/RBAC requirement) - not copied
/// from any prior codebase. Role/permission seeding and upsert helpers are
/// shared with ProductionBootstrapSeeder via SeedHelpers, so the two cannot
/// drift apart on RBAC.
/// </summary>
public static class DevelopmentSeeder
{
    public static async Task SeedAsync(CcmcDbContext db, IPasswordHasher passwordHasher, ILogger logger)
    {
        await SeedHelpers.SeedRolesAndPermissionsAsync(db);

        // --- Centres -----------------------------------------------------------
        var centreBlr = await SeedHelpers.UpsertCentreAsync(db, "BLR-CC-01", "Chilling Centre - Bangalore");
        var centreMys = await SeedHelpers.UpsertCentreAsync(db, "MYS-CC-01", "Chilling Centre - Mysore");

        // --- Users -------------------------------------------------------------
        var adminRole = await db.Roles.SingleAsync(r => r.Name == "Admin");
        var managerRole = await db.Roles.SingleAsync(r => r.Name == "Manager");
        var operatorRole = await db.Roles.SingleAsync(r => r.Name == "Operator");

        var (admin, _) = await SeedHelpers.UpsertUserAsync(db, passwordHasher, "admin@ccmc.local", "System Administrator", "Admin@12345");
        var (manager1, _) = await SeedHelpers.UpsertUserAsync(db, passwordHasher, "manager1@ccmc.local", "Bangalore Manager", "Manager@12345");
        var (operator1, _) = await SeedHelpers.UpsertUserAsync(db, passwordHasher, "operator1@ccmc.local", "Bangalore Operator", "Operator@12345");
        var (operator2, _) = await SeedHelpers.UpsertUserAsync(db, passwordHasher, "operator2@ccmc.local", "Mysore Operator", "Operator@12345");

        await SeedHelpers.AssignRoleAsync(db, admin.Id, adminRole.Id);
        await SeedHelpers.AssignRoleAsync(db, manager1.Id, managerRole.Id);
        await SeedHelpers.AssignRoleAsync(db, operator1.Id, operatorRole.Id);
        await SeedHelpers.AssignRoleAsync(db, operator2.Id, operatorRole.Id);

        await SeedHelpers.AssignCentreAsync(db, admin.Id, centreId: null, allCentres: true);
        await SeedHelpers.AssignCentreAsync(db, manager1.Id, centreBlr.Id, allCentres: false);
        await SeedHelpers.AssignCentreAsync(db, operator1.Id, centreBlr.Id, allCentres: false);
        await SeedHelpers.AssignCentreAsync(db, operator2.Id, centreMys.Id, allCentres: false);

        // --- Global quality rules - values taken directly from BRD v2 section 10 ----
        await SeedHelpers.UpsertGlobalRuleAsync(db, QualityParameter.Fat, 3.0m, 6.0m);
        await SeedHelpers.UpsertGlobalRuleAsync(db, QualityParameter.Snf, 8.0m, 10.0m);
        await SeedHelpers.UpsertGlobalRuleAsync(db, QualityParameter.Temperature, 0m, 10.0m);

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

    private static async Task UpsertSourceAsync(CcmcDbContext db, string code, string name, int centreId)
    {
        var exists = await db.Sources.AnyAsync(s => s.Code == code);
        if (!exists)
        {
            db.Sources.Add(new CCMC.Cloud.Domain.Entities.Source
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
            db.Vehicles.Add(new CCMC.Cloud.Domain.Entities.Vehicle
            {
                VehicleNumber = vehicleNumber, DriverName = driverName, DriverMobile = "9900000000",
                TankerNumber = $"TNK-{vehicleNumber[^4..]}", CapacityKg = 5000m, CentreId = centreId, Status = RecordStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
    }
}
