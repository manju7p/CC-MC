using CCMC.Application.Abstractions;
using CCMC.Domain.Devices;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Domain.Services;
using CCMC.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CCMC.Application.Reception;

public sealed record ReceptionContext(int CentreId, int SourceId, int VehicleId, int OperatorUserId);

/// <summary>
/// Result of the "Read Devices" step. Either device may come back unavailable
/// (not connected, or protocol not established - see DeviceProtocolNotEstablishedException)
/// without this being an error: the operator can enter that reading manually
/// (BRD v2 section 15, "Manual Fallback"). A failed device must never crash
/// the reception workflow.
/// </summary>
public sealed record ReadDevicesResult(
    WeightReading? Weight,
    string? WeightUnavailableReason,
    MilkQualityReading? Quality,
    string? QualityUnavailableReason)
{
    public bool WeightNeedsManualEntry => Weight is null;
    public bool QualityNeedsManualEntry => Quality is null;
}

public sealed record SaveReceptionInput(
    int CentreId,
    int SourceId,
    int VehicleId,
    int OperatorUserId,
    decimal QuantityKg,
    decimal Fat,
    decimal Snf,
    decimal Temperature,
    ReadingSource ReadingSource);

public sealed record SaveReceptionResult(MilkReceptionTransaction Transaction, bool WasNewlyCreated);

/// <summary>
/// Orchestrates the full local reception flow: read devices -&gt; validate
/// quality -&gt; save locally -&gt; queue for sync. Contains no manufacturer-
/// specific serial logic (that lives behind IWeighingScale/IMilkAnalyser in
/// CCMC.Infrastructure) and no UI logic (WPF code-behind calls this, not the
/// other way around) - see CLAUDE.md "Architecture Decisions".
/// </summary>
public sealed class ReceptionWorkflowService(
    IDeviceManager deviceManager,
    IQualityRuleRepository qualityRuleRepository,
    IReceptionRepository receptionRepository,
    IAuditLogRepository auditLogRepository,
    IIdempotencyKeyGenerator idempotencyKeyGenerator,
    IClock clock,
    ILogger<ReceptionWorkflowService> logger)
{
    private static readonly IReadOnlyList<QualityParameter> RequiredParameters =
        [QualityParameter.Fat, QualityParameter.Snf, QualityParameter.Temperature];

    /// <summary>
    /// Reads both devices concurrently (no BRD-specified operator sequence
    /// for the two devices - concurrent reads carry the fewest hidden
    /// ordering assumptions, same reasoning the legacy gateway design
    /// documented). Never throws for an unavailable/not-yet-protocol'd
    /// device - that is reported back as a manual-entry requirement.
    /// </summary>
    public async Task<ReadDevicesResult> ReadDevicesAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Reception capture started - reading weighing scale and milk analyser.");

        var weightTask = ReadWeightSafeAsync(cancellationToken);
        var qualityTask = ReadQualitySafeAsync(cancellationToken);
        await Task.WhenAll(weightTask, qualityTask);

        var (weight, weightError) = weightTask.Result;
        var (quality, qualityError) = qualityTask.Result;

        if (weightError is not null) logger.LogInformation("Weight unavailable: {Reason}", weightError);
        if (qualityError is not null) logger.LogInformation("Quality unavailable: {Reason}", qualityError);

        return new ReadDevicesResult(weight, weightError, quality, qualityError);
    }

    private async Task<(WeightReading? Reading, string? Error)> ReadWeightSafeAsync(CancellationToken cancellationToken)
    {
        var scale = deviceManager.WeighingScale;
        if (scale is null) return (null, "No weighing scale is configured.");

        try
        {
            return (await scale.ReadWeightAsync(cancellationToken), null);
        }
        catch (DeviceNotConnectedException ex)
        {
            return (null, ex.Message);
        }
        catch (DeviceProtocolNotEstablishedException ex)
        {
            return (null, ex.Message);
        }
        catch (DeviceParseException ex)
        {
            return (null, ex.Message);
        }
    }

    private async Task<(MilkQualityReading? Reading, string? Error)> ReadQualitySafeAsync(CancellationToken cancellationToken)
    {
        var analyser = deviceManager.MilkAnalyser;
        if (analyser is null) return (null, "No milk analyser is configured.");

        try
        {
            return (await analyser.ReadQualityAsync(cancellationToken), null);
        }
        catch (DeviceNotConnectedException ex)
        {
            return (null, ex.Message);
        }
        catch (DeviceProtocolNotEstablishedException ex)
        {
            return (null, ex.Message);
        }
        catch (DeviceParseException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Validates against resolved quality rules and saves the reception
    /// locally (SQLite) with a freshly generated idempotency key, atomically
    /// with its sync-outbox record. Local save always happens here,
    /// independent of cloud reachability - the sync engine picks this row up
    /// separately (see SyncEngineService).
    /// </summary>
    public async Task<SaveReceptionResult> ValidateAndSaveAsync(SaveReceptionInput input, CancellationToken cancellationToken)
    {
        var resolvedRules = await qualityRuleRepository.ResolveForCentreAsync(input.CentreId, RequiredParameters, cancellationToken);

        var ruleMap = RequiredParameters.ToDictionary(
            p => p,
            p => resolvedRules.TryGetValue(p, out var rule)
                ? new ResolvedQualityRule(rule.MinValue, rule.MaxValue)
                : throw new InvalidOperationException($"No quality rule resolved for {p} in centre {input.CentreId}."));

        var validation = QualityValidationService.Validate(
            new QualityReadingInput(input.Fat, input.Snf, input.Temperature), ruleMap);
        logger.LogInformation(
            "Reception validated for centre {CentreId}: {Status} (reason: {Reason})",
            input.CentreId, validation.Status, validation.Reason ?? "n/a");

        var now = clock.UtcNow;
        var transaction = new MilkReceptionTransaction
        {
            CentreId = input.CentreId,
            SourceId = input.SourceId,
            VehicleId = input.VehicleId,
            OperatorUserId = input.OperatorUserId,
            QuantityKg = input.QuantityKg,
            Fat = input.Fat,
            Snf = input.Snf,
            Temperature = input.Temperature,
            Status = validation.Status,
            ReadingSource = input.ReadingSource,
            Reason = validation.Reason,
            LocalIdempotencyKey = idempotencyKeyGenerator.NewKey(),
            CapturedAt = now,
            CreatedAt = now,
        };

        var created = await receptionRepository.CreateAsync(transaction, cancellationToken);

        await auditLogRepository.RecordAsync(
            new AuditLogEntry
            {
                UserId = input.OperatorUserId,
                CentreId = input.CentreId,
                Action = "RECEPTION_CREATE",
                ResourceType = "MilkReceptionTransaction",
                ResourceId = created.Transaction.LocalId.ToString(),
                Reason = validation.Reason,
                CreatedAt = now,
            },
            cancellationToken);

        logger.LogInformation(
            "Reception saved locally: localId={LocalId} status={Status} newlyCreated={WasNewlyCreated}",
            created.Transaction.LocalId, created.Transaction.Status, created.WasNewlyCreated);

        return new SaveReceptionResult(created.Transaction, created.WasNewlyCreated);
    }

    /// <summary>
    /// A Manager resolving a HOLD to ACCEPTED/REJECTED. Obeys the same
    /// local-first synchronization architecture as a reception create: the
    /// local transaction row, the override audit row, and its override_outbox
    /// row are all written atomically (see IReceptionRepository.ApplyOverrideAsync),
    /// then picked up by SyncEngineService.TickOverridesAsync once the
    /// underlying reception itself has a CloudTransactionId.
    /// </summary>
    public async Task OverrideAsync(long transactionLocalId, TransactionStatus newStatus, string reason, int performedByUserId, CancellationToken cancellationToken)
    {
        if (newStatus is not (TransactionStatus.Accepted or TransactionStatus.Rejected))
        {
            // Matches the cloud's own constraint (OverrideReceptionDto only
            // accepts ACCEPTED/REJECTED) - rejected here too so an invalid
            // value can never reach the sync engine's status mapping.
            throw new ArgumentException($"An override's new status must be Accepted or Rejected, not {newStatus}.", nameof(newStatus));
        }

        var transaction = await receptionRepository.GetByLocalIdAsync(transactionLocalId, cancellationToken)
            ?? throw new InvalidOperationException($"Reception transaction {transactionLocalId} not found locally.");

        if (transaction.Status != TransactionStatus.Hold)
        {
            throw new InvalidOperationException("Only a transaction currently on HOLD can be overridden.");
        }

        var now = clock.UtcNow;
        var @override = new TransactionOverride
        {
            TransactionLocalId = transactionLocalId,
            OriginalStatus = TransactionStatus.Hold,
            NewStatus = newStatus,
            PerformedByUserId = performedByUserId,
            Reason = reason,
            CreatedAt = now,
        };

        var outboxRecord = await receptionRepository.ApplyOverrideAsync(@override, cancellationToken);
        logger.LogInformation(
            "Override queued for sync: localId={LocalId} -> {NewStatus} (override_outbox id={OutboxId})",
            transactionLocalId, newStatus, outboxRecord.Id);

        await auditLogRepository.RecordAsync(
            new AuditLogEntry
            {
                UserId = performedByUserId,
                CentreId = transaction.CentreId,
                Action = "RECEPTION_OVERRIDE",
                ResourceType = "MilkReceptionTransaction",
                ResourceId = transactionLocalId.ToString(),
                Reason = reason,
                CreatedAt = now,
            },
            cancellationToken);

        logger.LogInformation(
            "Reception overridden: localId={LocalId} Hold -> {NewStatus} by user {PerformedByUserId}",
            transactionLocalId, newStatus, performedByUserId);
    }
}
