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

    /// <summary>
    /// Milk analyser fields beyond Fat/Snf (density/CLR, added-water percent,
    /// protein) - additive, nullable: not every reception has an analyser
    /// reading, and rows created before this column existed have none. See
    /// CCMC.Domain.Entities.MilkReceptionTransaction (Windows client) for the
    /// matching local-schema fields this mirrors.
    /// </summary>
    public decimal? Clr { get; set; }
    public decimal? Water { get; set; }
    public decimal? Protein { get; set; }

    /// <summary>The exact analyser payload Fat/Snf/Clr/Water/Protein were decoded from - preserved verbatim.</summary>
    public string? RawAnalyserPayload { get; set; }

    /// <summary>
    /// BRD v5.0 section 25 Rate/Amount - trusted verbatim from the Windows
    /// client, the same treatment as Fat/Snf/Clr/Water/Protein: these were
    /// computed once at capture time (possibly fully offline) against
    /// whatever rate formula settings were locally cached then, and that
    /// historical value must be preserved even if the cloud's own
    /// RateFormulaSettings later change - the cloud does not, and must not,
    /// recompute them independently on sync.
    /// </summary>
    public decimal? Rate { get; set; }
    public decimal? Amount { get; set; }

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
