using CCMC.Cloud.Application.Audit;
using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Application.Common;
using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Domain.Services;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CCMC.Cloud.Application.Reception;

public sealed record CreateReceptionCommand(
    int CentreId, int SourceId, int VehicleId,
    decimal QuantityKg, decimal Fat, decimal Snf, decimal Temperature,
    string? LocalIdempotencyKey,
    TransactionStatus? Status = null,
    decimal? Clr = null, decimal? Water = null, decimal? Protein = null, string? RawAnalyserPayload = null,
    decimal? Rate = null, decimal? Amount = null);

public enum CreateReceptionOutcome { Created, Duplicate }

public sealed record CreateReceptionResult(CreateReceptionOutcome Outcome, MilkReceptionTransaction Transaction);

public sealed record OverrideReceptionCommand(TransactionStatus NewStatus, string Reason);

/// <summary>
/// The cloud's authoritative reception-mutation logic. CreateAsync mirrors
/// the exact idempotency contract the Windows client's HttpCloudApiClient/
/// SyncEngineService already assume (context.md/CLAUDE.md "API Contract" -
/// verified against CCMC.Contracts.Dtos.CreateReceptionRequestDto/
/// ReceptionTransactionDto and CCMC.Infrastructure.Sync.HttpResponseClassifier):
/// a request with a localIdempotencyKey that already exists returns the
/// existing row unchanged (Duplicate, still 201) if the payload matches, or
/// throws IdempotencyConflictException (409) naming every conflicting field
/// if it does not - never a silent overwrite, never a second create.
///
/// The actual correctness boundary is the database's own UNIQUE constraint
/// (ix_milk_reception_transactions_local_idempotency_key), not an
/// application-level "check then insert": the insert is attempted directly,
/// and a Postgres unique-violation on that specific index is what identifies
/// a genuine duplicate - Postgres itself serializes two concurrent inserts
/// racing for the same key, so there is no window for a query, then a
/// insert to see stale state.
/// </summary>
public sealed class ReceptionService(
    CcmcDbContext db,
    IAuditService audit,
    ILogger<ReceptionService> logger)
{
    private const string LocalIdempotencyKeyConstraintName = "ix_milk_reception_transactions_local_idempotency_key";

    private static readonly QualityParameter[] RequiredParameters =
        [QualityParameter.Fat, QualityParameter.Snf, QualityParameter.Temperature];

    public async Task<MilkReceptionTransaction> GetByIdAsync(RequestUser user, int id, CancellationToken cancellationToken)
    {
        var transaction = await db.MilkReceptionTransactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Reception transaction {id} not found.");
        CentreAccessGuard.AssertCanAccess(user.CentreAccess, transaction.CentreId);
        return transaction;
    }

    public async Task<IReadOnlyList<MilkReceptionTransaction>> ListAsync(RequestUser user, CancellationToken cancellationToken)
    {
        var query = db.MilkReceptionTransactions.AsNoTracking().OrderByDescending(t => t.ReceivedAt).Take(100);

        if (user.CentreAccess.AllCentres)
        {
            return await query.ToListAsync(cancellationToken);
        }

        if (user.CentreAccess.CentreIds.Count == 0) return [];

        return await query.Where(t => user.CentreAccess.CentreIds.Contains(t.CentreId)).ToListAsync(cancellationToken);
    }

    public async Task<CreateReceptionResult> CreateAsync(RequestUser user, CreateReceptionCommand cmd, CancellationToken cancellationToken)
    {
        CentreAccessGuard.AssertCanAccess(user.CentreAccess, cmd.CentreId);

        var centre = await db.ChillingCentres.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cmd.CentreId, cancellationToken)
            ?? throw new ValidationException("Centre not found.");

        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == cmd.SourceId, cancellationToken);
        if (source is null || source.CentreId != cmd.CentreId) throw new ValidationException("Source not found for this centre.");
        if (source.Status != RecordStatus.Active) throw new ValidationException("Source is inactive.");

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(v => v.Id == cmd.VehicleId, cancellationToken);
        if (vehicle is null || vehicle.CentreId != cmd.CentreId) throw new ValidationException("Vehicle not found for this centre.");
        if (vehicle.Status != RecordStatus.Active) throw new ValidationException("Vehicle is inactive.");

        if (cmd.Status == TransactionStatus.Rejected)
        {
            // Matches the client-side invariant exactly (TransactionStatus's own
            // doc comment, both sides): Rejected is only ever reached via an
            // override of a prior Hold, never a direct create.
            throw new ValidationException("A reception cannot be created directly with status Rejected - reject an existing HOLD via the override endpoint instead.");
        }

        var rules = await ResolveRulesAsync(cmd.CentreId, cancellationToken);
        var validation = QualityValidationService.Validate(new QualityReadingInput(cmd.Fat, cmd.Snf, cmd.Temperature), rules);

        // If the caller (the Windows client, going forward) already made an
        // explicit human ACCEPT/HOLD decision, that decision is authoritative -
        // it may legitimately differ from this automatic suggestion (e.g. the
        // operator judged the analyser's Water/Protein reading unacceptable
        // even though Fat/Snf/Temperature alone are in range, or overrode a
        // borderline HOLD). The automatic suggestion is still computed and
        // still recorded (dormant, not deleted - see CLAUDE.md/STATUS.md "Milk
        // Analyser + Quality Decision Flow"). A caller that omits Status
        // (none exists today, but the field is optional for backward
        // compatibility) gets the original, fully-automatic behavior unchanged.
        var decidedStatus = cmd.Status ?? validation.Status;
        var reason = cmd.Status is { } decided && decided != validation.Status
            ? (validation.Reason is null
                ? $"Operator decision ({decided}) overrides the automatic quality suggestion of {validation.Status}."
                : $"Operator decision ({decided}) overrides the automatic quality suggestion of {validation.Status}: {validation.Reason}")
            : validation.Reason;

        var now = DateTimeOffset.UtcNow;
        var entity = new MilkReceptionTransaction
        {
            // Placeholder, replaced with {centreCode}-{id} once the row has an id (below) -
            // guarantees uniqueness by construction, no cross-request numbering race.
            TransactionNumber = $"PENDING-{Guid.NewGuid():N}",
            CentreId = cmd.CentreId,
            SourceId = cmd.SourceId,
            VehicleId = cmd.VehicleId,
            OperatorUserId = user.Id,
            QuantityKg = cmd.QuantityKg,
            Fat = cmd.Fat,
            Snf = cmd.Snf,
            Temperature = cmd.Temperature,
            Clr = cmd.Clr,
            Water = cmd.Water,
            Protein = cmd.Protein,
            RawAnalyserPayload = cmd.RawAnalyserPayload,
            // Trusted verbatim from the client, same treatment as Fat/Snf/Clr/
            // Water/Protein - see MilkReceptionTransaction.Rate's doc comment
            // for why the cloud never recomputes these independently.
            Rate = cmd.Rate,
            Amount = cmd.Amount,
            Status = decidedStatus,
            ReadingSource = ReadingSource.Manual,
            Reason = reason,
            LocalIdempotencyKey = cmd.LocalIdempotencyKey,
            ReceivedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (string.IsNullOrEmpty(cmd.LocalIdempotencyKey))
        {
            return await InsertAndFinalizeAsync(user, centre, entity, reason, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await InsertAndFinalizeAsync(user, centre, entity, reason, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException ex) when (IsLocalIdempotencyKeyViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();

            // The insert was rejected by the database's own unique constraint - a row
            // with this key is guaranteed already committed and visible to us now.
            var existing = await db.MilkReceptionTransactions.AsNoTracking()
                .SingleAsync(t => t.LocalIdempotencyKey == cmd.LocalIdempotencyKey, cancellationToken);

            var conflicts = FindConflicts(existing, cmd);
            if (conflicts.Count > 0)
            {
                throw new IdempotencyConflictException(
                    $"localIdempotencyKey \"{cmd.LocalIdempotencyKey}\" was already used for a transaction with different data " +
                    $"(conflicting field(s): {string.Join(", ", conflicts)}). Retrying with the same idempotency key must resubmit " +
                    "the SAME logical payload - this looks like a caller bug, not a legitimate retry.",
                    conflicts, existing.Id);
            }

            logger.LogInformation("Idempotent retry for localIdempotencyKey {Key} - returning existing transaction {Id}.", cmd.LocalIdempotencyKey, existing.Id);
            return new CreateReceptionResult(CreateReceptionOutcome.Duplicate, existing);
        }
    }

    private async Task<CreateReceptionResult> InsertAndFinalizeAsync(
        RequestUser user, ChillingCentre centre, MilkReceptionTransaction entity, string? reason,
        CancellationToken cancellationToken)
    {
        db.MilkReceptionTransactions.Add(entity);
        await db.SaveChangesAsync(cancellationToken); // assigns entity.Id, or throws DbUpdateException on unique violation

        entity.TransactionNumber = $"{centre.Code}-{entity.Id}";

        await audit.RecordAsync(
            new AuditEntry(user.Id, entity.CentreId, "RECEPTION_CREATE", "MilkReceptionTransaction", entity.Id.ToString(), Reason: reason),
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Reception created: id={Id} transactionNumber={TransactionNumber} status={Status} centre={CentreId}",
            entity.Id, entity.TransactionNumber, entity.Status, entity.CentreId);

        return new CreateReceptionResult(CreateReceptionOutcome.Created, entity);
    }

    public async Task<MilkReceptionTransaction> OverrideAsync(RequestUser user, int transactionId, OverrideReceptionCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.NewStatus is not (TransactionStatus.Accepted or TransactionStatus.Rejected))
        {
            throw new ValidationException("newStatus must be Accepted or Rejected.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var transaction = await db.MilkReceptionTransactions.SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken)
            ?? throw new NotFoundException($"Reception transaction {transactionId} not found.");

        CentreAccessGuard.AssertCanAccess(user.CentreAccess, transaction.CentreId);

        if (transaction.Status != TransactionStatus.Hold)
        {
            // No idempotency key exists for this endpoint in the current client
            // contract (documented gap - see CLAUDE.md "Architecture Decisions" /
            // "Known Gaps"): a retried override request cannot be distinguished
            // from a genuine second attempt, so this is always a terminal
            // conflict, never silently treated as "already succeeded."
            throw new BusinessConflictException("Only a transaction currently on HOLD can be overridden.");
        }

        var originalStatus = transaction.Status;
        transaction.Status = cmd.NewStatus;
        transaction.Reason = cmd.Reason;
        transaction.UpdatedAt = DateTimeOffset.UtcNow;

        db.TransactionOverrides.Add(new TransactionOverride
        {
            TransactionId = transactionId,
            OriginalStatus = originalStatus,
            NewStatus = cmd.NewStatus,
            PerformedByUserId = user.Id,
            Reason = cmd.Reason,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await audit.RecordAsync(
            new AuditEntry(
                user.Id, transaction.CentreId, "RECEPTION_OVERRIDE", "MilkReceptionTransaction", transactionId.ToString(),
                OldValueJson: $"{{\"status\":\"{originalStatus}\"}}", NewValueJson: $"{{\"status\":\"{cmd.NewStatus}\"}}", Reason: cmd.Reason),
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Reception overridden: id={Id} {OriginalStatus} -> {NewStatus} by user {UserId}",
            transactionId, originalStatus, cmd.NewStatus, user.Id);

        return transaction;
    }

    private async Task<IReadOnlyDictionary<QualityParameter, ResolvedQualityRule>> ResolveRulesAsync(int centreId, CancellationToken cancellationToken)
    {
        var candidates = await db.QualityRules.AsNoTracking()
            .Where(r => RequiredParameters.Contains(r.Parameter) && (r.CentreId == centreId || r.CentreId == null))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<QualityParameter, ResolvedQualityRule>();
        foreach (var parameter in RequiredParameters)
        {
            // Centre-specific overrides global.
            var rule = candidates.FirstOrDefault(r => r.Parameter == parameter && r.CentreId == centreId)
                       ?? candidates.FirstOrDefault(r => r.Parameter == parameter && r.CentreId is null);
            if (rule is not null)
            {
                result[parameter] = new ResolvedQualityRule(rule.MinValue, rule.MaxValue);
            }
        }
        return result;
    }

    /// <summary>
    /// Mirrors the exact comparison the Windows client's own local idempotency
    /// check already uses (context.md/CLAUDE.md - both sides must agree on
    /// what "the same payload" means): centreId/sourceId/vehicleId/quantityKg/
    /// fat/snf/temperature. OperatorUserId is deliberately NOT compared - see
    /// CreateReceptionCommand's absence of that field: the cloud always
    /// attributes a synced reception to whichever authenticated user's token
    /// performed the sync, which may legitimately differ between the original
    /// attempt and a retry (e.g. after a re-login), without that being a real conflict.
    /// </summary>
    private static List<string> FindConflicts(MilkReceptionTransaction existing, CreateReceptionCommand incoming)
    {
        var conflicts = new List<string>();
        if (existing.CentreId != incoming.CentreId) conflicts.Add(nameof(existing.CentreId));
        if (existing.SourceId != incoming.SourceId) conflicts.Add(nameof(existing.SourceId));
        if (existing.VehicleId != incoming.VehicleId) conflicts.Add(nameof(existing.VehicleId));
        if (existing.QuantityKg != incoming.QuantityKg) conflicts.Add(nameof(existing.QuantityKg));
        if (existing.Fat != incoming.Fat) conflicts.Add(nameof(existing.Fat));
        if (existing.Snf != incoming.Snf) conflicts.Add(nameof(existing.Snf));
        if (existing.Temperature != incoming.Temperature) conflicts.Add(nameof(existing.Temperature));
        if (incoming.Status is { } status && existing.Status != status) conflicts.Add(nameof(existing.Status));
        return conflicts;
    }

    private static bool IsLocalIdempotencyKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        pg.ConstraintName == LocalIdempotencyKeyConstraintName;
}
