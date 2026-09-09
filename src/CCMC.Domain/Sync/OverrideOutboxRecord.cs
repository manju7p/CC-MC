using CCMC.Domain.Enums;

namespace CCMC.Domain.Sync;

/// <summary>
/// Durable outbox row for a manager override mutation, mirroring
/// OutboxRecord's design (same state machine, same retry/backoff strategy)
/// but for POST /reception/:id/override instead of POST /reception. Kept
/// as a separate table/type from OutboxRecord (not a discriminated union in
/// one table) because the payload shape and sync precondition differ: an
/// override cannot sync until its PARENT reception has already synced and
/// has a CloudTransactionId (see IOverrideOutboxRepository.GetEligibleAsync).
///
/// Unlike reception creation, the cloud's override endpoint has no
/// idempotency-key mechanism - see CLAUDE.md "Architecture Decisions" for
/// the documented contract gap this implies (a terminal/4xx response here
/// is NOT auto-retried or auto-assumed-successful; it is marked FAILED for
/// manual/ops review, since this implementation does not guess at a
/// guarantee the cloud contract does not provide).
/// </summary>
public sealed class OverrideOutboxRecord
{
    public long Id { get; set; }
    public required long OverrideId { get; init; }
    public required long TransactionLocalId { get; init; }
    public required TransactionStatus NewStatus { get; init; }
    public required string Reason { get; init; }
    public required OutboxStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public required DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
