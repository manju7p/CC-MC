using CCMC.Domain.Sync;

namespace CCMC.Application.Abstractions;

public interface IOutboxRepository
{
    Task<IReadOnlyList<OutboxRecord>> GetEligibleAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Atomically PENDING -&gt; PROCESSING. Returns false if another caller already claimed it (structural race guard).</summary>
    Task<bool> TryClaimAsync(long outboxId, DateTimeOffset claimedAt, CancellationToken cancellationToken);

    Task MarkSyncedAsync(long outboxId, CancellationToken cancellationToken);

    Task MarkRetryAsync(long outboxId, DateTimeOffset nextAttemptAt, string error, CancellationToken cancellationToken);

    Task MarkFailedAsync(long outboxId, string error, CancellationToken cancellationToken);

    /// <summary>Requeues any row still PROCESSING (orphaned by a crash) back to PENDING. Called on startup.</summary>
    Task<int> RecoverStaleProcessingAsync(DateTimeOffset now, TimeSpan staleThreshold, CancellationToken cancellationToken);

    Task<int> CountPendingAsync(CancellationToken cancellationToken);
}
