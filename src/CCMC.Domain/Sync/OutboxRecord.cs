using CCMC.Domain.Enums;

namespace CCMC.Domain.Sync;

/// <summary>
/// One row per local transaction (1:1), tracking sync ATTEMPT state - distinct
/// from MilkReceptionTransaction.CloudTransactionId, which is the single
/// "is this synced" signal (see context.md "Database Responsibilities").
/// Design adapted from the proven legacy gateway outbox pattern documented in
/// context.md (not copied code - this is an independent C# implementation).
/// </summary>
public sealed class OutboxRecord
{
    public long Id { get; set; }
    public required long TransactionLocalId { get; init; }
    public required string LocalIdempotencyKey { get; init; }
    public required OutboxStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public required DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
