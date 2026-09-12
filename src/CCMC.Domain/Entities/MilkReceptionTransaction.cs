using CCMC.Domain.Enums;

namespace CCMC.Domain.Entities;

/// <summary>
/// A locally captured reception. Mirrors the cloud's CreateReceptionRequest/
/// MilkReceptionTransaction shape (documented in context.md "Database
/// Responsibilities") so the two stay logically compatible, per the "SQLite
/// and PostgreSQL are not disposable/unrelated schemas" requirement.
/// </summary>
public sealed class MilkReceptionTransaction
{
    /// <summary>Local autoincrement id - never sent to the cloud. Assigned by the repository after insert.</summary>
    public long LocalId { get; set; }

    /// <summary>
    /// Assigned by the cloud once synced ({centreCode}-{cloudId}, per
    /// docs/assumptions.md #transaction-numbering). Null until CloudTransactionId
    /// is set.
    /// </summary>
    public string? TransactionNumber { get; set; }

    public required int CentreId { get; init; }
    public required int SourceId { get; init; }
    public required int VehicleId { get; init; }
    public required int OperatorUserId { get; init; }

    public required decimal QuantityKg { get; init; }
    public required decimal Fat { get; init; }
    public required decimal Snf { get; init; }
    public required decimal Temperature { get; init; }

    /// <summary>
    /// Milk analyser fields beyond Fat/Snf (density/CLR, added-water percent,
    /// protein) - additive, nullable: a reception saved before these existed,
    /// or one entered without an analyser reading at all, simply has none of
    /// them. Not part of QualityValidationService's range checks (see
    /// ReceptionWorkflowService.ValidateAndSaveAsync's doc comment) - captured
    /// and synced for record-keeping/operator judgment, same as the BRD's own
    /// "optional parameters" concept for MilkQualityReading.
    /// </summary>
    public decimal? Clr { get; init; }
    public decimal? Water { get; init; }
    public decimal? Protein { get; init; }

    /// <summary>
    /// The exact analyser payload this reception's Fat/Snf/Clr/Water/Protein
    /// were decoded from (device or manual test input - see
    /// Kam98A2AAnalyserFrameParser) - preserved verbatim so a suspicious
    /// reading can always be traced back to exactly what the device sent,
    /// never just the parsed numbers (CLAUDE.md "no fabricated device data"
    /// extends to "never discard the evidence either").
    /// </summary>
    public string? RawAnalyserPayload { get; init; }

    /// <summary>
    /// BRD v5.0 section 25 Rate/Amount, computed once at capture time from
    /// Fat/Snf/QuantityKg against the centre's rate formula settings resolved
    /// at that moment (see ReceptionWorkflowService.ValidateAndSaveAsync) -
    /// never recomputed from today's configuration. Nullable, not required,
    /// for the same reason Clr/Water/Protein are: a reception saved before
    /// this feature existed simply has neither value, distinct from a
    /// reception where the calculation legitimately produced 0 (BRD's own
    /// "not configured" rule - see RateCalculationService).
    /// </summary>
    public decimal? Rate { get; init; }
    public decimal? Amount { get; init; }

    public required TransactionStatus Status { get; set; }
    public required ReadingSource ReadingSource { get; init; }
    public string? Reason { get; set; }

    /// <summary>
    /// Generated exactly once, at capture time, never regenerated on a sync
    /// retry (see context.md "Synchronization Model" - this is the field the
    /// cloud's POST /reception's localIdempotencyKey is populated from).
    /// </summary>
    public required string LocalIdempotencyKey { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null = not yet synced. This is the single source of truth for "is this synced" (see context.md).</summary>
    public int? CloudTransactionId { get; set; }
}
