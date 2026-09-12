using CCMC.Application.Abstractions;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace CCMC.Infrastructure.Persistence.Repositories;

/// <summary>
/// Atomic reception + outbox creation, with database-level idempotency
/// (local_idempotency_key UNIQUE). Design adapted from the legacy gateway's
/// proven local-transactions pattern (context.md "Important Existing
/// Components") - independently implemented against SQLite here.
/// </summary>
public sealed class ReceptionRepository(SqliteConnectionFactory connectionFactory) : IReceptionRepository
{
    public async Task<CreateLocalTransactionResult> CreateAsync(MilkReceptionTransaction transaction, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existing = await FindByKeyAsync(connection, tx, transaction.LocalIdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var conflicts = FindConflicts(existing, transaction);
            if (conflicts.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                throw new IdempotencyKeyConflictException(transaction.LocalIdempotencyKey, conflicts);
            }

            var existingOutbox = await GetOutboxForTransactionAsync(connection, tx, existing.LocalId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Local transaction {existing.LocalId} exists with no matching outbox row - data integrity violation.");

            await tx.CommitAsync(cancellationToken);
            return new CreateLocalTransactionResult(existing, existingOutbox, WasNewlyCreated: false);
        }

        var localId = await InsertTransactionAsync(connection, tx, transaction, cancellationToken);
        transaction.LocalId = localId;

        var now = DateTimeOffset.UtcNow;
        var outbox = new OutboxRecord
        {
            TransactionLocalId = localId,
            LocalIdempotencyKey = transaction.LocalIdempotencyKey,
            Status = OutboxStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        outbox.Id = await InsertOutboxAsync(connection, tx, outbox, cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new CreateLocalTransactionResult(transaction, outbox, WasNewlyCreated: true);
    }

    public async Task<MilkReceptionTransaction?> GetByLocalIdAsync(long localId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE local_id = $localId;";
        command.Parameters.AddWithValue("$localId", localId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<MilkReceptionTransaction>> ListRecentAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY captured_at DESC LIMIT $take;";
        command.Parameters.AddWithValue("$take", take);
        var results = new List<MilkReceptionTransaction>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }
        return results;
    }

    public async Task UpdateAfterSyncAsync(long localId, int cloudTransactionId, string transactionNumber, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE local_transactions
            SET cloud_transaction_id = $cloudId, transaction_number = $number
            WHERE local_id = $localId;
            """;
        command.Parameters.AddWithValue("$cloudId", cloudTransactionId);
        command.Parameters.AddWithValue("$number", transactionNumber);
        command.Parameters.AddWithValue("$localId", localId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OverrideOutboxRecord> ApplyOverrideAsync(TransactionOverride @override, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        using (var updateStatus = connection.CreateCommand())
        {
            updateStatus.Transaction = tx;
            updateStatus.CommandText = """
                UPDATE local_transactions
                SET status = $status, reason = $reason
                WHERE local_id = $localId;
                """;
            updateStatus.Parameters.AddWithValue("$status", @override.NewStatus.ToString());
            updateStatus.Parameters.AddWithValue("$reason", @override.Reason);
            updateStatus.Parameters.AddWithValue("$localId", @override.TransactionLocalId);
            await updateStatus.ExecuteNonQueryAsync(cancellationToken);
        }

        long overrideId;
        using (var insertOverride = connection.CreateCommand())
        {
            insertOverride.Transaction = tx;
            insertOverride.CommandText = """
                INSERT INTO transaction_overrides
                    (transaction_local_id, original_status, new_status, performed_by_user_id, reason, created_at)
                VALUES
                    ($transactionLocalId, $originalStatus, $newStatus, $performedBy, $reason, $createdAt);
                SELECT last_insert_rowid();
                """;
            insertOverride.Parameters.AddWithValue("$transactionLocalId", @override.TransactionLocalId);
            insertOverride.Parameters.AddWithValue("$originalStatus", @override.OriginalStatus.ToString());
            insertOverride.Parameters.AddWithValue("$newStatus", @override.NewStatus.ToString());
            insertOverride.Parameters.AddWithValue("$performedBy", @override.PerformedByUserId);
            insertOverride.Parameters.AddWithValue("$reason", @override.Reason);
            insertOverride.Parameters.AddWithValue("$createdAt", @override.CreatedAt.ToString("O"));
            overrideId = (long)(await insertOverride.ExecuteScalarAsync(cancellationToken))!;
        }

        var now = DateTimeOffset.UtcNow;
        var outboxRecord = new OverrideOutboxRecord
        {
            OverrideId = overrideId,
            TransactionLocalId = @override.TransactionLocalId,
            NewStatus = @override.NewStatus,
            Reason = @override.Reason,
            Status = OutboxStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        using (var insertOutbox = connection.CreateCommand())
        {
            insertOutbox.Transaction = tx;
            insertOutbox.CommandText = """
                INSERT INTO override_outbox
                    (override_id, transaction_local_id, new_status, reason, status, attempt_count,
                     last_attempt_at, next_attempt_at, last_error, claimed_at, created_at, updated_at)
                VALUES
                    ($overrideId, $transactionLocalId, $newStatus, $reason, $status, $attemptCount,
                     NULL, $nextAttemptAt, NULL, NULL, $createdAt, $updatedAt);
                SELECT last_insert_rowid();
                """;
            insertOutbox.Parameters.AddWithValue("$overrideId", overrideId);
            insertOutbox.Parameters.AddWithValue("$transactionLocalId", outboxRecord.TransactionLocalId);
            insertOutbox.Parameters.AddWithValue("$newStatus", outboxRecord.NewStatus.ToString());
            insertOutbox.Parameters.AddWithValue("$reason", outboxRecord.Reason);
            insertOutbox.Parameters.AddWithValue("$status", outboxRecord.Status.ToString());
            insertOutbox.Parameters.AddWithValue("$attemptCount", outboxRecord.AttemptCount);
            insertOutbox.Parameters.AddWithValue("$nextAttemptAt", outboxRecord.NextAttemptAt.ToString("O"));
            insertOutbox.Parameters.AddWithValue("$createdAt", outboxRecord.CreatedAt.ToString("O"));
            insertOutbox.Parameters.AddWithValue("$updatedAt", outboxRecord.UpdatedAt.ToString("O"));
            outboxRecord.Id = (long)(await insertOutbox.ExecuteScalarAsync(cancellationToken))!;
        }

        await tx.CommitAsync(cancellationToken);
        return outboxRecord;
    }

    private const string SelectColumns = """
        SELECT local_id, transaction_number, centre_id, source_id, vehicle_id, operator_user_id,
               quantity_kg, fat, snf, temperature, status, reading_source, reason,
               local_idempotency_key, captured_at, created_at, cloud_transaction_id,
               clr, water, protein, raw_analyser_payload, rate, amount
        FROM local_transactions
        """;

    private static async Task<MilkReceptionTransaction?> FindByKeyAsync(
        SqliteConnection connection, SqliteTransaction tx, string key, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = SelectColumns + " WHERE local_idempotency_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static async Task<OutboxRecord?> GetOutboxForTransactionAsync(
        SqliteConnection connection, SqliteTransaction tx, long transactionLocalId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = OutboxRepository.SelectColumns + " WHERE transaction_local_id = $id;";
        command.Parameters.AddWithValue("$id", transactionLocalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? OutboxRepository.Map(reader) : null;
    }

    private static async Task<long> InsertTransactionAsync(
        SqliteConnection connection, SqliteTransaction tx, MilkReceptionTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO local_transactions
                (transaction_number, centre_id, source_id, vehicle_id, operator_user_id,
                 quantity_kg, fat, snf, temperature, status, reading_source, reason,
                 local_idempotency_key, captured_at, created_at, cloud_transaction_id,
                 clr, water, protein, raw_analyser_payload, rate, amount)
            VALUES
                ($number, $centreId, $sourceId, $vehicleId, $operatorUserId,
                 $quantityKg, $fat, $snf, $temperature, $status, $readingSource, $reason,
                 $key, $capturedAt, $createdAt, NULL,
                 $clr, $water, $protein, $rawAnalyserPayload, $rate, $amount);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$number", (object?)transaction.TransactionNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$centreId", transaction.CentreId);
        command.Parameters.AddWithValue("$sourceId", transaction.SourceId);
        command.Parameters.AddWithValue("$vehicleId", transaction.VehicleId);
        command.Parameters.AddWithValue("$operatorUserId", transaction.OperatorUserId);
        command.Parameters.AddWithValue("$quantityKg", transaction.QuantityKg);
        command.Parameters.AddWithValue("$fat", transaction.Fat);
        command.Parameters.AddWithValue("$snf", transaction.Snf);
        command.Parameters.AddWithValue("$temperature", transaction.Temperature);
        command.Parameters.AddWithValue("$status", transaction.Status.ToString());
        command.Parameters.AddWithValue("$readingSource", transaction.ReadingSource.ToString());
        command.Parameters.AddWithValue("$reason", (object?)transaction.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", transaction.LocalIdempotencyKey);
        command.Parameters.AddWithValue("$capturedAt", transaction.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$createdAt", transaction.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$clr", (object?)transaction.Clr ?? DBNull.Value);
        command.Parameters.AddWithValue("$water", (object?)transaction.Water ?? DBNull.Value);
        command.Parameters.AddWithValue("$protein", (object?)transaction.Protein ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawAnalyserPayload", (object?)transaction.RawAnalyserPayload ?? DBNull.Value);
        command.Parameters.AddWithValue("$rate", (object?)transaction.Rate ?? DBNull.Value);
        command.Parameters.AddWithValue("$amount", (object?)transaction.Amount ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return (long)result!;
    }

    private static async Task<long> InsertOutboxAsync(
        SqliteConnection connection, SqliteTransaction tx, OutboxRecord outbox, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO outbox_records
                (transaction_local_id, local_idempotency_key, status, attempt_count,
                 last_attempt_at, next_attempt_at, last_error, claimed_at, created_at, updated_at)
            VALUES
                ($transactionLocalId, $key, $status, $attemptCount,
                 NULL, $nextAttemptAt, NULL, NULL, $createdAt, $updatedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$transactionLocalId", outbox.TransactionLocalId);
        command.Parameters.AddWithValue("$key", outbox.LocalIdempotencyKey);
        command.Parameters.AddWithValue("$status", outbox.Status.ToString());
        command.Parameters.AddWithValue("$attemptCount", outbox.AttemptCount);
        command.Parameters.AddWithValue("$nextAttemptAt", outbox.NextAttemptAt.ToString("O"));
        command.Parameters.AddWithValue("$createdAt", outbox.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", outbox.UpdatedAt.ToString("O"));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return (long)result!;
    }

    /// <summary>
    /// Mirrors the cloud's own conflict comparison (documented in
    /// context.md), plus CapturedAt (the legacy gateway design's extra local
    /// safeguard - see context.md "Important Existing Components"): two
    /// genuinely different captures that happen to produce identical
    /// weight/fat/snf/temperature values must not be silently treated as
    /// "the same payload" just because the numbers coincide.
    /// </summary>
    private static List<string> FindConflicts(MilkReceptionTransaction existing, MilkReceptionTransaction incoming)
    {
        var conflicts = new List<string>();
        if (existing.CentreId != incoming.CentreId) conflicts.Add(nameof(existing.CentreId));
        if (existing.SourceId != incoming.SourceId) conflicts.Add(nameof(existing.SourceId));
        if (existing.VehicleId != incoming.VehicleId) conflicts.Add(nameof(existing.VehicleId));
        if (existing.QuantityKg != incoming.QuantityKg) conflicts.Add(nameof(existing.QuantityKg));
        if (existing.Fat != incoming.Fat) conflicts.Add(nameof(existing.Fat));
        if (existing.Snf != incoming.Snf) conflicts.Add(nameof(existing.Snf));
        if (existing.Temperature != incoming.Temperature) conflicts.Add(nameof(existing.Temperature));
        if (existing.CapturedAt != incoming.CapturedAt) conflicts.Add(nameof(existing.CapturedAt));
        if (existing.Status != incoming.Status) conflicts.Add(nameof(existing.Status));
        return conflicts;
    }

    private static MilkReceptionTransaction Map(SqliteDataReader reader) => new()
    {
        LocalId = reader.GetInt64(0),
        TransactionNumber = reader.IsDBNull(1) ? null : reader.GetString(1),
        CentreId = reader.GetInt32(2),
        SourceId = reader.GetInt32(3),
        VehicleId = reader.GetInt32(4),
        OperatorUserId = reader.GetInt32(5),
        QuantityKg = (decimal)reader.GetDouble(6),
        Fat = (decimal)reader.GetDouble(7),
        Snf = (decimal)reader.GetDouble(8),
        Temperature = (decimal)reader.GetDouble(9),
        Status = Enum.Parse<TransactionStatus>(reader.GetString(10)),
        ReadingSource = Enum.Parse<ReadingSource>(reader.GetString(11)),
        Reason = reader.IsDBNull(12) ? null : reader.GetString(12),
        LocalIdempotencyKey = reader.GetString(13),
        CapturedAt = DateTimeOffset.Parse(reader.GetString(14)),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(15)),
        CloudTransactionId = reader.IsDBNull(16) ? null : reader.GetInt32(16),
        Clr = reader.IsDBNull(17) ? null : (decimal)reader.GetDouble(17),
        Water = reader.IsDBNull(18) ? null : (decimal)reader.GetDouble(18),
        Protein = reader.IsDBNull(19) ? null : (decimal)reader.GetDouble(19),
        RawAnalyserPayload = reader.IsDBNull(20) ? null : reader.GetString(20),
        Rate = reader.IsDBNull(21) ? null : (decimal)reader.GetDouble(21),
        Amount = reader.IsDBNull(22) ? null : (decimal)reader.GetDouble(22),
    };
}
