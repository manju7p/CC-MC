using CCMC.Cloud.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Infrastructure.Persistence;

/// <summary>
/// The cloud's only PostgreSQL access point - the Windows client never
/// touches this database directly, only through the HTTPS API built on top
/// of it (CLAUDE.md/context.md "Constraints"). Column-level decimal
/// precision is documented per-property below (never floating point for
/// milk quantities/quality measurements - this session's explicit
/// requirement).
/// </summary>
public sealed class CcmcDbContext(DbContextOptions<CcmcDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserCentreAssignment> UserCentreAssignments => Set<UserCentreAssignment>();
    public DbSet<ChillingCentre> ChillingCentres => Set<ChillingCentre>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<QualityRule> QualityRules => Set<QualityRule>();
    public DbSet<RateFormulaSettings> RateFormulaSettings => Set<RateFormulaSettings>();
    public DbSet<MilkReceptionTransaction> MilkReceptionTransactions => Set<MilkReceptionTransaction>();
    public DbSet<TransactionOverride> TransactionOverrides => Set<TransactionOverride>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // --- Users / RBAC -----------------------------------------------
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Email).IsRequired().HasMaxLength(256);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.PasswordHash).IsRequired();
            e.Property(u => u.FullName).IsRequired().HasMaxLength(200);
            e.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).IsRequired().HasMaxLength(100);
            e.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasKey(p => p.Id);
            e.Property(p => p.Code).IsRequired().HasMaxLength(100);
            e.HasIndex(p => p.Code).IsUnique();
            e.Property(p => p.Module).IsRequired().HasMaxLength(100);
            e.Property(p => p.Action).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(rp => new { rp.RoleId, rp.PermissionId });
            e.HasOne(rp => rp.Role).WithMany(r => r.RolePermissions).HasForeignKey(rp => rp.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(rp => rp.Permission).WithMany().HasForeignKey(rp => rp.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(ur => new { ur.UserId, ur.RoleId });
            e.HasOne(ur => ur.User).WithMany(u => u.UserRoles).HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ur => ur.Role).WithMany().HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserCentreAssignment>(e =>
        {
            e.ToTable("user_centre_assignments");
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.UserId, a.CentreId }).IsUnique();
            e.HasOne(a => a.User).WithMany(u => u.CentreAssignments).HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Centre).WithMany().HasForeignKey(a => a.CentreId).OnDelete(DeleteBehavior.Cascade);
        });

        // --- Master data --------------------------------------------------
        modelBuilder.Entity<ChillingCentre>(e =>
        {
            e.ToTable("chilling_centres");
            e.HasKey(c => c.Id);
            e.Property(c => c.Code).IsRequired().HasMaxLength(50);
            e.HasIndex(c => c.Code).IsUnique();
            e.Property(c => c.Name).IsRequired().HasMaxLength(200);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<Source>(e =>
        {
            e.ToTable("sources");
            e.HasKey(s => s.Id);
            e.Property(s => s.Code).IsRequired().HasMaxLength(50);
            e.HasIndex(s => s.Code).IsUnique();
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(s => s.Centre).WithMany().HasForeignKey(s => s.CentreId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => s.CentreId);
        });

        modelBuilder.Entity<Vehicle>(e =>
        {
            e.ToTable("vehicles");
            e.HasKey(v => v.Id);
            e.Property(v => v.VehicleNumber).IsRequired().HasMaxLength(50);
            e.HasIndex(v => v.VehicleNumber).IsUnique();
            // NUMERIC(10,2): up to 99,999,999.99 kg - no real tanker approaches this; leaves generous headroom.
            e.Property(v => v.CapacityKg).HasPrecision(10, 2);
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(v => v.Centre).WithMany().HasForeignKey(v => v.CentreId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(v => v.CentreId);
        });

        modelBuilder.Entity<QualityRule>(e =>
        {
            e.ToTable("quality_rules");
            e.HasKey(q => q.Id);
            e.Property(q => q.Parameter).HasConversion<string>().HasMaxLength(20);
            // NUMERIC(6,2): realistic FAT/SNF percentage and temperature (°C) ranges at 2-decimal lab precision.
            e.Property(q => q.MinValue).HasPrecision(6, 2);
            e.Property(q => q.MaxValue).HasPrecision(6, 2);
            e.HasIndex(q => new { q.Parameter, q.CentreId }).IsUnique();
            e.HasOne(q => q.Centre).WithMany().HasForeignKey(q => q.CentreId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RateFormulaSettings>(e =>
        {
            e.ToTable("rate_formula_settings");
            e.HasKey(r => r.Id);
            e.Property(r => r.RateType).HasConversion<string>().HasMaxLength(20);
            // NUMERIC(10,2): rate-formula price components (Value1/Value2/TsRate) -
            // generous headroom above any realistic per-unit rupee value, same
            // reasoning as Vehicle.CapacityKg.
            e.Property(r => r.Value1).HasPrecision(10, 2);
            e.Property(r => r.Value2).HasPrecision(10, 2);
            e.Property(r => r.TsRate).HasPrecision(10, 2);
            // One row per centre (null = one global default) - same
            // centre-specific-over-global convention as QualityRule, and the
            // same NULLS-DISTINCT looseness on the global row that QualityRule's
            // own unique index already accepts (see QualityRule's index above).
            e.HasIndex(r => r.CentreId).IsUnique();
            e.HasOne(r => r.Centre).WithMany().HasForeignKey(r => r.CentreId).OnDelete(DeleteBehavior.Restrict);
        });

        // --- Reception -----------------------------------------------------
        modelBuilder.Entity<MilkReceptionTransaction>(e =>
        {
            e.ToTable("milk_reception_transactions");
            e.HasKey(t => t.Id);
            e.Property(t => t.TransactionNumber).IsRequired().HasMaxLength(50);
            e.HasIndex(t => t.TransactionNumber).IsUnique();
            // NUMERIC(10,2) kg - matches Vehicle.CapacityKg's reasoning.
            e.Property(t => t.QuantityKg).HasPrecision(10, 2);
            // NUMERIC(5,2): FAT/SNF percentages and temperature °C - up to 999.99, comfortably
            // above any realistic value while keeping 2-decimal lab precision.
            e.Property(t => t.Fat).HasPrecision(5, 2);
            e.Property(t => t.Snf).HasPrecision(5, 2);
            e.Property(t => t.Temperature).HasPrecision(5, 2);
            // NUMERIC(5,2): milk analyser fields (Clr up to ~99.99, Water/Protein
            // percentages) - same precision reasoning as Fat/Snf/Temperature above.
            e.Property(t => t.Clr).HasPrecision(5, 2);
            e.Property(t => t.Water).HasPrecision(5, 2);
            e.Property(t => t.Protein).HasPrecision(5, 2);
            e.Property(t => t.RawAnalyserPayload).HasMaxLength(64);
            // NUMERIC(10,2)/(14,2): Rate is a per-unit price (same headroom as
            // RateFormulaSettings' Value1/Value2/TsRate); Amount = Rate x
            // QuantityKg needs more headroom for the product.
            e.Property(t => t.Rate).HasPrecision(10, 2);
            e.Property(t => t.Amount).HasPrecision(14, 2);
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.ReadingSource).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.LocalIdempotencyKey).HasMaxLength(200);
            // Named explicitly (not left to EF's default convention) so ReceptionService
            // can reliably recognize *this specific* unique-violation by Postgres
            // constraint name when classifying a duplicate-key insert attempt.
            // Postgres allows multiple NULLs under a unique index - only non-null keys are deduplicated.
            e.HasIndex(t => t.LocalIdempotencyKey).IsUnique().HasDatabaseName("ix_milk_reception_transactions_local_idempotency_key");
            e.HasIndex(t => new { t.CentreId, t.ReceivedAt });
            e.HasIndex(t => new { t.CentreId, t.Status });
            e.HasOne(t => t.Centre).WithMany().HasForeignKey(t => t.CentreId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Source).WithMany().HasForeignKey(t => t.SourceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Vehicle).WithMany().HasForeignKey(t => t.VehicleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Operator).WithMany().HasForeignKey(t => t.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TransactionOverride>(e =>
        {
            e.ToTable("transaction_overrides");
            e.HasKey(o => o.Id);
            e.Property(o => o.OriginalStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.NewStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.Reason).IsRequired();
            e.HasOne(o => o.Transaction).WithMany().HasForeignKey(o => o.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(a => a.Id);
            e.Property(a => a.Action).IsRequired().HasMaxLength(100);
            e.Property(a => a.ResourceType).IsRequired().HasMaxLength(100);
            e.Property(a => a.ResourceId).IsRequired().HasMaxLength(100);
            e.HasIndex(a => new { a.UserId, a.CreatedAt });
            e.HasIndex(a => new { a.ResourceType, a.ResourceId });
        });
    }
}
