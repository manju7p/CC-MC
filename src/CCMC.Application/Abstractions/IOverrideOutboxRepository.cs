using CCMC.Domain.Sync;

namespace CCMC.Application.Abstractions;

/// <summary>
/// Same state-machine shape as IOutboxRepository, for override mutations.
/// GetEligibleAsync must only return rows whose PARENT reception has already
/// synced (has a non-null CloudTransactionId) - an override cannot be pushed
/// to the cloud before the reception it targets exists there.
/// </summary>
public interface IOverrideOutboxRepository
{
    Task<IReadOnlyList<OverrideOutboxRecord>> GetEligibleAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task<bool> TryClaimAsync(long id, DateTimeOffset claimedAt, CancellationToken cancellationToken);

    Task MarkSyncedAsync(long id, CancellationToken cancellationToken);

    Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string error, CancellationToken cancellationToken);

    Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken);

    Task<int> RecoverStaleProcessingAsync(DateTimeOffset now, TimeSpan staleThreshold, CancellationToken cancellationToken);

    Task<int> CountPendingAsync(CancellationToken cancellationToken);
}
