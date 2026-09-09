using CCMC.Cloud.Domain.Enums;

namespace CCMC.Cloud.Domain.Entities;

/// <summary>
/// The authoritative cloud reception record. Quantities/quality use
/// NUMERIC(10,2)/NUMERIC(5,2) (never floating point) - see
/// CloudDbContext's OnModelCreating for the exact column precision and
/// rationale. LocalIdempotencyKey is nullable+unique: null for anything not
/// submitted via the idempotent sync path (there is currently no non-idempotent
/// path exposed to the Windows client, but the column stays nullable so a
/// future non-idempotent caller isn't forced to fabricate a key).
/// </summary>
public sealed class MilkReceptionTransaction
{
    public int Id { get; set; }
    public required string TransactionNumber { get; set; }
    public int CentreId { get; set; }
    public int SourceId { get; set; }
    public int VehicleId { get; set; }
    public int OperatorUserId { get; set; }

    /// <summary>NUMERIC(10,2) kilograms.</summary>
    public decimal QuantityKg { get; set; }

    /// <summary>NUMERIC(5,2) percent.</summary>
    public decimal Fat { get; set; }

    /// <summary>NUMERIC(5,2) percent.</summary>
    public decimal Snf { get; set; }

    /// <summary>NUMERIC(5,2) degrees Celsius.</summary>
    public decimal Temperature { get; set; }

    public TransactionStatus Status { get; set; }
    public ReadingSource ReadingSource { get; set; } = ReadingSource.Manual;
    public string? Reason { get; set; }

    public string? LocalIdempotencyKey { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ChillingCentre Centre { get; set; } = null!;
    public Source Source { get; set; } = null!;
    public Vehicle Vehicle { get; set; } = null!;
    public User Operator { get; set; } = null!;
}

/// <summary>A Manager resolving a HOLD transaction to ACCEPTED/REJECTED.</summary>
public sealed class TransactionOverride
{
    public int Id { get; set; }
    public int TransactionId { get; set; }
    public TransactionStatus OriginalStatus { get; set; }
    public TransactionStatus NewStatus { get; set; }
    public int PerformedByUserId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public MilkReceptionTransaction Transaction { get; set; } = null!;
}

public sealed class AuditLog
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public int? CentreId { get; set; }
    public required string Action { get; set; }
    public required string ResourceType { get; set; }
    public required string ResourceId { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
