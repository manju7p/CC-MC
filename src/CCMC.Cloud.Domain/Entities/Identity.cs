using CCMC.Cloud.Domain.Enums;

namespace CCMC.Cloud.Domain.Entities;

public sealed class User
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string FullName { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<UserRole> UserRoles { get; set; } = [];
    public List<UserCentreAssignment> CentreAssignments { get; set; } = [];
}

public sealed class Role
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsSystemDefault { get; set; } = true;

    public List<RolePermission> RolePermissions { get; set; } = [];
}

public sealed class Permission
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string Module { get; set; }
    public required string Action { get; set; }
}

/// <summary>Composite key (RoleId, PermissionId).</summary>
public sealed class RolePermission
{
    public int RoleId { get; set; }
    public int PermissionId { get; set; }

    public Role Role { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
}

/// <summary>Composite key (UserId, RoleId).</summary>
public sealed class UserRole
{
    public int UserId { get; set; }
    public int RoleId { get; set; }

    public User User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

/// <summary>
/// One row per centre a user is scoped to, plus a separate AllCentres=true/
/// CentreId=null row for organization-wide access (only one such row per
/// user is an application-level convention, enforced by the seeder - a
/// unique index cannot express "at most one NULL" portably, and Postgres
/// itself allows multiple NULLs under a unique constraint anyway).
/// </summary>
public sealed class UserCentreAssignment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? CentreId { get; set; }
    public bool AllCentres { get; set; }

    public User User { get; set; } = null!;
    public ChillingCentre? Centre { get; set; }
}
