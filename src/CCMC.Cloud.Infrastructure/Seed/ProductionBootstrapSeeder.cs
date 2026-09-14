using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Auth;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CCMC.Cloud.Infrastructure.Seed;

/// <summary>
/// Runs in ANY environment (unlike DevelopmentSeeder, which is gated to
/// Development only), but only when Bootstrap:AdminEmail is actually
/// configured - see ProductionBootstrapOptions and Program.cs. Every
/// identity/credential comes from configuration, never invented here.
/// Idempotent and safe to leave configured across restarts: an existing
/// user's PasswordHash is never overwritten (SeedHelpers.UpsertUserAsync),
/// so this never silently resets a real production password.
/// </summary>
public static class ProductionBootstrapSeeder
{
    public static async Task SeedAsync(CcmcDbContext db, IPasswordHasher passwordHasher, ILogger logger, ProductionBootstrapOptions options)
    {
        await SeedHelpers.SeedRolesAndPermissionsAsync(db);

        int? centreId = null;
        if (!string.IsNullOrWhiteSpace(options.CentreCode))
        {
            var centre = await SeedHelpers.UpsertCentreAsync(db, options.CentreCode, options.CentreName!);
            centreId = centre.Id;
        }

        var adminRole = await db.Roles.SingleAsync(r => r.Name == "Admin");
        var (admin, adminCreated) = await SeedHelpers.UpsertUserAsync(db, passwordHasher, options.AdminEmail, options.AdminFullName, options.AdminPassword);
        await SeedHelpers.AssignRoleAsync(db, admin.Id, adminRole.Id);
        await SeedHelpers.AssignCentreAsync(db, admin.Id, centreId: null, allCentres: true);
        LogAccountOutcome(logger, "Admin", options.AdminEmail, adminCreated);

        if (!string.IsNullOrWhiteSpace(options.ManagerEmail))
        {
            var managerRole = await db.Roles.SingleAsync(r => r.Name == "Manager");
            var (manager, managerCreated) = await SeedHelpers.UpsertUserAsync(db, passwordHasher, options.ManagerEmail, options.ManagerFullName ?? "Manager", options.ManagerPassword!);
            await SeedHelpers.AssignRoleAsync(db, manager.Id, managerRole.Id);
            await SeedHelpers.AssignCentreAsync(db, manager.Id, centreId!.Value, allCentres: false);
            LogAccountOutcome(logger, "Manager", options.ManagerEmail, managerCreated);
        }

        if (!string.IsNullOrWhiteSpace(options.OperatorEmail))
        {
            var operatorRole = await db.Roles.SingleAsync(r => r.Name == "Operator");
            var (op, operatorCreated) = await SeedHelpers.UpsertUserAsync(db, passwordHasher, options.OperatorEmail, options.OperatorFullName ?? "Operator", options.OperatorPassword!);
            await SeedHelpers.AssignRoleAsync(db, op.Id, operatorRole.Id);
            await SeedHelpers.AssignCentreAsync(db, op.Id, centreId!.Value, allCentres: false);
            LogAccountOutcome(logger, "Operator", options.OperatorEmail, operatorCreated);
        }

        // BRD v2 section 10's own FAT/SNF/Temperature limits - a documented
        // business rule, not a fabricated default (see DevelopmentSeeder).
        await SeedHelpers.UpsertGlobalRuleAsync(db, QualityParameter.Fat, 3.0m, 6.0m);
        await SeedHelpers.UpsertGlobalRuleAsync(db, QualityParameter.Snf, 8.0m, 10.0m);
        await SeedHelpers.UpsertGlobalRuleAsync(db, QualityParameter.Temperature, 0m, 10.0m);

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Production bootstrap check complete. No password is ever logged. Rotate any password you supplied here as soon as it has " +
            "served its purpose - no in-app password-change endpoint exists yet, so rotation today requires direct database access.");
    }

    private static void LogAccountOutcome(ILogger logger, string role, string email, bool created)
    {
        if (created)
        {
            logger.LogInformation("Production bootstrap: created {Role} account {Email}.", role, email);
        }
        else
        {
            logger.LogInformation("Production bootstrap: {Role} account {Email} already exists - left unchanged (password not modified).", role, email);
        }
    }
}
