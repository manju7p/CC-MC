namespace CCMC.Domain.Entities;

/// <summary>
/// Local audit trail. BRD v2 section 14 requires login/logout, transaction
/// create/modify/cancel, device configuration, quality rules, manual entry,
/// overrides, source/vehicle changes and synchronization to be audited.
/// </summary>
public sealed class AuditLogEntry
{
    public long Id { get; init; }
    public int? UserId { get; init; }
    public int? CentreId { get; init; }
    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public required string ResourceId { get; init; }
    public string? OldValueJson { get; init; }
    public string? NewValueJson { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
