using CCMC.Domain.Entities;
using CCMC.Domain.Sync;

namespace CCMC.Application.Abstractions;

public sealed record CreateLocalTransactionResult(MilkReceptionTransaction Transaction, OutboxRecord Outbox, bool WasNewlyCreated);

/// <summary>
/// Local SQLite persistence for receptions. CreateAsync must be atomic: the
/// transaction row and its outbox row either both exist or neither does (see
/// context.md "Database Responsibilities" - the legacy gateway's proven
/// BEGIN IMMEDIATE pattern, adapted here, not copied).
/// </summary>
public interface IReceptionRepository
{
    /// <summary>
    /// Idempotent create: if a row with the same LocalIdempotencyKey already
    /// exists, returns it unchanged (WasNewlyCreated=false) when the payload
    /// matches, or throws IdempotencyKeyConflictException when it does not.
    /// </summary>
    Task<CreateLocalTransactionResult> CreateAsync(MilkReceptionTransaction transaction, CancellationToken cancellationToken);

    Task<MilkReceptionTransaction?> GetByLocalIdAsync(long localId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MilkReceptionTransaction>> ListRecentAsync(int take, CancellationToken cancellationToken);

    Task UpdateAfterSyncAsync(long localId, int cloudTransactionId, string transactionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a manager override (HOLD -&gt; ACCEPTED/REJECTED) atomically:
    /// updates the local transaction row, inserts the override audit row, AND
    /// inserts its override_outbox row, all in one transaction - so the
    /// override obeys the same local-first synchronization architecture as a
    /// reception create (see CLAUDE.md "Architecture Decisions" - Manager
    /// Override Sync). Returns the created outbox row so callers can log its id.
    /// </summary>
    Task<OverrideOutboxRecord> ApplyOverrideAsync(TransactionOverride @override, CancellationToken cancellationToken);
}

public sealed class IdempotencyKeyConflictException(string key, IReadOnlyList<string> conflictingFields)
    : Exception($"Local idempotency key '{key}' already used with different data: {string.Join(", ", conflictingFields)}")
{
    public string Key { get; } = key;
    public IReadOnlyList<string> ConflictingFields { get; } = conflictingFields;
}
