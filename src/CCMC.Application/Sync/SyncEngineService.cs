using CCMC.Application.Abstractions;
using CCMC.Contracts.Dtos;
using CCMC.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace CCMC.Application.Sync;

public sealed record SyncEngineOptions
{
    public int MaxAttempts { get; init; } = 10;
    public TimeSpan StaleProcessingThreshold { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan BaseBackoffDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoffDelay { get; init; } = TimeSpan.FromMinutes(5);
    public int TickBatchSize { get; init; } = 20;
}

public sealed record SyncTickResult(
    int Attempted, int Synced, int Retried, int Failed, int Skipped,
    int OverridesAttempted = 0, int OverridesSynced = 0, int OverridesRetried = 0, int OverridesFailed = 0);

/// <summary>
/// Pushes PENDING local transactions AND PENDING manager overrides to the
/// cloud via ICloudApiClient, idempotently where the cloud contract supports
/// it. State machine and recovery strategy adapted from the legacy gateway's
/// proven design (documented in context.md "Synchronization Model") -
/// independently implemented here, not copied code.
///
/// Requires an active, ONLINE operator session (ISessionStore.Current, with
/// IsOffline == false) to obtain a usable bearer token - see CLAUDE.md
/// "Architecture Decisions" (Offline Operator Login / Background Sync): an
/// offline-authenticated session has no valid access token, so a tick with
/// nobody signed in, or only an offline session, is a documented no-op
/// (Skipped), not a crash or a silently invented auth mechanism. There is
/// still no separate unattended/service-account credential - by product
/// decision, sync always rides the currently signed-in operator's own token.
/// </summary>
public sealed class SyncEngineService(
    IOutboxRepository outboxRepository,
    IOverrideOutboxRepository overrideOutboxRepository,
    IReceptionRepository receptionRepository,
    ICloudApiClient cloudApiClient,
    ISessionStore sessionStore,
    IClock clock,
    ILogger<SyncEngineService> logger,
    SyncEngineOptions? options = null)
{
    private readonly SyncEngineOptions _options = options ?? new SyncEngineOptions();

    /// <summary>Call once at app startup, before the first tick - requeues any row orphaned by a prior crash.</summary>
    public async Task RecoverAtStartupAsync(CancellationToken cancellationToken)
    {
        await outboxRepository.RecoverStaleProcessingAsync(clock.UtcNow, TimeSpan.Zero, cancellationToken);
        await overrideOutboxRepository.RecoverStaleProcessingAsync(clock.UtcNow, TimeSpan.Zero, cancellationToken);
    }

    public async Task<SyncTickResult> TickAsync(CancellationToken cancellationToken)
    {
        await outboxRepository.RecoverStaleProcessingAsync(clock.UtcNow, _options.StaleProcessingThreshold, cancellationToken);
        await overrideOutboxRepository.RecoverStaleProcessingAsync(clock.UtcNow, _options.StaleProcessingThreshold, cancellationToken);

        var session = sessionStore.Current;
        if (session is null || session.IsOffline)
        {
            logger.LogDebug(
                "Sync tick skipped - {Reason}.", session is null ? "no active session" : "session is offline (no valid access token)");
            return new SyncTickResult(0, 0, 0, 0, Skipped: 1);
        }

        var (attempted, synced, retried, failed) = await TickReceptionsAsync(session.AccessToken, cancellationToken);
        var (overridesAttempted, overridesSynced, overridesRetried, overridesFailed) =
            await TickOverridesAsync(session.AccessToken, cancellationToken);

        if (attempted > 0 || overridesAttempted > 0)
        {
            logger.LogInformation(
                "Sync tick complete: receptions[attempted={Attempted} synced={Synced} retried={Retried} failed={Failed}] " +
                "overrides[attempted={OverridesAttempted} synced={OverridesSynced} retried={OverridesRetried} failed={OverridesFailed}]",
                attempted, synced, retried, failed, overridesAttempted, overridesSynced, overridesRetried, overridesFailed);
        }

        return new SyncTickResult(
            attempted, synced, retried, failed, Skipped: 0,
            overridesAttempted, overridesSynced, overridesRetried, overridesFailed);
    }

    private async Task<(int Attempted, int Synced, int Retried, int Failed)> TickReceptionsAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        var eligible = await outboxRepository.GetEligibleAsync(clock.UtcNow, _options.TickBatchSize, cancellationToken);
        int synced = 0, retried = 0, failed = 0, attempted = 0;

        foreach (var record in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var claimed = await outboxRepository.TryClaimAsync(record.Id, clock.UtcNow, cancellationToken);
            if (!claimed) continue; // another caller claimed it first - structural race guard, not expected single-process

            attempted++;
            var outcome = await AttemptSyncAsync(record, accessToken, cancellationToken);
            switch (outcome)
            {
                case SyncAttemptOutcome.Synced: synced++; break;
                case SyncAttemptOutcome.Retried: retried++; break;
                case SyncAttemptOutcome.Failed: failed++; break;
            }
        }

        return (attempted, synced, retried, failed);
    }

    private async Task<(int Attempted, int Synced, int Retried, int Failed)> TickOverridesAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        var eligible = await overrideOutboxRepository.GetEligibleAsync(clock.UtcNow, _options.TickBatchSize, cancellationToken);
        int synced = 0, retried = 0, failed = 0, attempted = 0;

        foreach (var record in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var claimed = await overrideOutboxRepository.TryClaimAsync(record.Id, clock.UtcNow, cancellationToken);
            if (!claimed) continue;

            attempted++;
            var outcome = await AttemptOverrideSyncAsync(record, accessToken, cancellationToken);
            switch (outcome)
            {
                case SyncAttemptOutcome.Synced: synced++; break;
                case SyncAttemptOutcome.Retried: retried++; break;
                case SyncAttemptOutcome.Failed: failed++; break;
            }
        }

        return (attempted, synced, retried, failed);
    }

    private enum SyncAttemptOutcome { Synced, Retried, Failed }

    private async Task<SyncAttemptOutcome> AttemptSyncAsync(OutboxRecord record, string accessToken, CancellationToken cancellationToken)
    {
        var transaction = await receptionRepository.GetByLocalIdAsync(record.TransactionLocalId, cancellationToken);
        if (transaction is null)
        {
            // Local row vanished under us - cannot recover automatically; do not
            // spin forever. Mark FAILED for manual/ops review rather than retrying
            // a request that references data that no longer exists locally.
            await outboxRepository.MarkFailedAsync(record.Id, "Local transaction row not found.", cancellationToken);
            return SyncAttemptOutcome.Failed;
        }

        // CreateReceptionRequestDto has no ReadingSource field - the cloud contract
        // (documented in context.md) doesn't accept one; it always persists
        // ReadingSource.MANUAL server-side for a gateway-style client submission.
        //
        // Status is the ORIGINAL creation-time decision, which is always
        // Accepted or Hold by construction (TransactionStatus's own invariant -
        // Rejected is only ever reached via an override of a prior Hold, see
        // ReceptionWorkflowService.RejectAtReceptionAsync). If the local row's
        // CURRENT status is Rejected, that means an override already ran
        // locally (ahead of this create even syncing - the override's own
        // outbox row is separately gated on this create having synced first,
        // see OverrideOutboxRepository.GetEligibleAsync) - Hold is what was
        // actually decided at creation time, so that is what this request
        // must carry; the override syncs its own change afterward, same as always.
        var creationStatus = transaction.Status == Domain.Enums.TransactionStatus.Rejected
            ? Domain.Enums.TransactionStatus.Hold
            : transaction.Status;

        var request = new CreateReceptionRequestDto
        {
            CentreId = transaction.CentreId,
            SourceId = transaction.SourceId,
            VehicleId = transaction.VehicleId,
            QuantityKg = transaction.QuantityKg,
            Fat = transaction.Fat,
            Snf = transaction.Snf,
            Temperature = transaction.Temperature,
            Status = MapStatus(creationStatus),
            Clr = transaction.Clr,
            Water = transaction.Water,
            Protein = transaction.Protein,
            RawAnalyserPayload = transaction.RawAnalyserPayload,
            LocalIdempotencyKey = transaction.LocalIdempotencyKey, // never regenerated - see docstring
        };

        logger.LogDebug("Sync attempt for local transaction {LocalId} (outbox {OutboxId})...", transaction.LocalId, record.Id);
        var result = await cloudApiClient.CreateReceptionAsync(accessToken, request, cancellationToken);

        switch (result.Outcome)
        {
            case Abstractions.CloudReceptionOutcome.Created:
            case Abstractions.CloudReceptionOutcome.Duplicate:
            {
                var dto = result.Transaction!;
                await receptionRepository.UpdateAfterSyncAsync(transaction.LocalId, dto.Id, dto.TransactionNumber, cancellationToken);
                await outboxRepository.MarkSyncedAsync(record.Id, cancellationToken);
                logger.LogInformation(
                    "Sync succeeded ({Outcome}): local transaction {LocalId} -> cloud transaction {CloudId} ({TransactionNumber})",
                    result.Outcome, transaction.LocalId, dto.Id, dto.TransactionNumber);
                return SyncAttemptOutcome.Synced;
            }

            case Abstractions.CloudReceptionOutcome.Conflict:
            case Abstractions.CloudReceptionOutcome.Terminal:
            {
                await outboxRepository.MarkFailedAsync(record.Id, result.ErrorMessage ?? "Terminal cloud error.", cancellationToken);
                logger.LogError(
                    "Sync permanently failed for local transaction {LocalId}: {Outcome} - {ErrorMessage}",
                    transaction.LocalId, result.Outcome, result.ErrorMessage);
                return SyncAttemptOutcome.Failed;
            }

            case Abstractions.CloudReceptionOutcome.AuthRetryable:
            case Abstractions.CloudReceptionOutcome.Retryable:
            default:
            {
                var nextAttemptNumber = record.AttemptCount + 1;
                if (nextAttemptNumber >= _options.MaxAttempts)
                {
                    await outboxRepository.MarkFailedAsync(
                        record.Id, $"Exceeded max attempts ({_options.MaxAttempts}). Last error: {result.ErrorMessage}", cancellationToken);
                    logger.LogError(
                        "Sync failed permanently after {MaxAttempts} attempts for local transaction {LocalId}: {ErrorMessage}",
                        _options.MaxAttempts, transaction.LocalId, result.ErrorMessage);
                    return SyncAttemptOutcome.Failed;
                }

                var delay = Backoff.Compute(nextAttemptNumber, _options.BaseBackoffDelay, _options.MaxBackoffDelay);
                await outboxRepository.MarkRetryAsync(
                    record.Id, clock.UtcNow + delay, result.ErrorMessage ?? "Retryable cloud/network error.", cancellationToken);
                logger.LogWarning(
                    "Sync attempt {AttemptNumber} failed for local transaction {LocalId}, retrying in {DelaySeconds:0.#}s: {ErrorMessage}",
                    nextAttemptNumber, transaction.LocalId, delay.TotalSeconds, result.ErrorMessage);
                return SyncAttemptOutcome.Retried;
            }
        }
    }

    private async Task<SyncAttemptOutcome> AttemptOverrideSyncAsync(
        OverrideOutboxRecord record, string accessToken, CancellationToken cancellationToken)
    {
        var transaction = await receptionRepository.GetByLocalIdAsync(record.TransactionLocalId, cancellationToken);
        if (transaction?.CloudTransactionId is not { } cloudTransactionId)
        {
            // GetEligibleAsync's join already filters out rows whose parent
            // hasn't synced yet - reaching here means the parent's cloud id
            // disappeared or the row itself vanished between the eligibility
            // query and this claim. Requeue rather than fail outright; the
            // next tick's eligibility query will simply skip it again until
            // (or if) the parent gets a CloudTransactionId.
            await overrideOutboxRepository.MarkRetryAsync(
                record.Id, clock.UtcNow + _options.BaseBackoffDelay, "Parent reception not yet synced to the cloud.", cancellationToken);
            return SyncAttemptOutcome.Retried;
        }

        var request = new Contracts.Dtos.OverrideReceptionRequestDto
        {
            NewStatus = MapStatus(record.NewStatus),
            Reason = record.Reason,
        };

        logger.LogDebug(
            "Override sync attempt for local transaction {LocalId} -> cloud transaction {CloudId} (override_outbox {OutboxId})...",
            record.TransactionLocalId, cloudTransactionId, record.Id);
        var result = await cloudApiClient.OverrideReceptionAsync(accessToken, cloudTransactionId, request, cancellationToken);

        switch (result.Outcome)
        {
            case Abstractions.CloudMutationOutcome.Success:
            {
                await overrideOutboxRepository.MarkSyncedAsync(record.Id, cancellationToken);
                logger.LogInformation(
                    "Override synced: local transaction {LocalId} -> cloud transaction {CloudId} now {NewStatus}",
                    record.TransactionLocalId, cloudTransactionId, record.NewStatus);
                return SyncAttemptOutcome.Synced;
            }

            case Abstractions.CloudMutationOutcome.Terminal:
            {
                // Documented cloud contract gap (CLAUDE.md "Architecture Decisions"):
                // POST /reception/:id/override has no idempotency key, so a terminal
                // 4xx here is genuinely ambiguous on a retry (it may mean an earlier
                // attempt of THIS override already succeeded, or that a different
                // actor changed the transaction). Not guessed at - marked FAILED for
                // manual/ops review rather than silently assumed either way.
                await overrideOutboxRepository.MarkFailedAsync(record.Id, result.ErrorMessage ?? "Terminal cloud error.", cancellationToken);
                logger.LogError(
                    "Override sync permanently failed for local transaction {LocalId} (cloud {CloudId}): {ErrorMessage}. " +
                    "This cloud endpoint has no idempotency key - if this was a retry, verify via the cloud/audit log whether " +
                    "an earlier attempt already applied this override before assuming it did not.",
                    record.TransactionLocalId, cloudTransactionId, result.ErrorMessage);
                return SyncAttemptOutcome.Failed;
            }

            case Abstractions.CloudMutationOutcome.AuthRetryable:
            case Abstractions.CloudMutationOutcome.Retryable:
            default:
            {
                var nextAttemptNumber = record.AttemptCount + 1;
                if (nextAttemptNumber >= _options.MaxAttempts)
                {
                    await overrideOutboxRepository.MarkFailedAsync(
                        record.Id, $"Exceeded max attempts ({_options.MaxAttempts}). Last error: {result.ErrorMessage}", cancellationToken);
                    logger.LogError(
                        "Override sync failed permanently after {MaxAttempts} attempts for local transaction {LocalId}: {ErrorMessage}",
                        _options.MaxAttempts, record.TransactionLocalId, result.ErrorMessage);
                    return SyncAttemptOutcome.Failed;
                }

                var delay = Backoff.Compute(nextAttemptNumber, _options.BaseBackoffDelay, _options.MaxBackoffDelay);
                await overrideOutboxRepository.MarkRetryAsync(
                    record.Id, clock.UtcNow + delay, result.ErrorMessage ?? "Retryable cloud/network error.", cancellationToken);
                logger.LogWarning(
                    "Override sync attempt {AttemptNumber} failed for local transaction {LocalId}, retrying in {DelaySeconds:0.#}s: {ErrorMessage}",
                    nextAttemptNumber, record.TransactionLocalId, delay.TotalSeconds, result.ErrorMessage);
                return SyncAttemptOutcome.Retried;
            }
        }
    }

    private static Contracts.Enums.TransactionStatus MapStatus(Domain.Enums.TransactionStatus status) => status switch
    {
        Domain.Enums.TransactionStatus.Accepted => Contracts.Enums.TransactionStatus.ACCEPTED,
        Domain.Enums.TransactionStatus.Rejected => Contracts.Enums.TransactionStatus.REJECTED,
        Domain.Enums.TransactionStatus.Hold => Contracts.Enums.TransactionStatus.HOLD,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
