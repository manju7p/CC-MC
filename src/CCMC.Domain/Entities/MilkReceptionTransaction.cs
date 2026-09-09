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
