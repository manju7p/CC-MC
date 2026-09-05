using CCMC.Application.Abstractions;
using CCMC.Domain.Enums;
using CCMC.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace CCMC.Infrastructure.Persistence.Repositories;

public sealed class OutboxRepository(SqliteConnectionFactory connectionFactory) : IOutboxRepository
{
    internal const string SelectColumns = """
        SELECT id, transaction_local_id, local_idempotency_key, status, attempt_count,
               last_attempt_at, next_attempt_at, last_error, claimed_at, created_at, updated_at
        FROM outbox_records
        """;

    public async Task<IReadOnlyList<OutboxRecord>> GetEligibleAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + """

             WHERE status = $pending AND next_attempt_at <= $now
             ORDER BY next_attempt_at ASC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<OutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }
        return results;
    }

    public async Task<bool> TryClaimAsync(long outboxId, DateTimeOffset claimedAt, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        // Atomic, conditional UPDATE guarded by status - a double-claim is a
        // structural non-issue even in a single-process app, defending a
        // future change to that assumption without silently reintroducing a
        // race (same reasoning as the legacy gateway's documented design).
        command.CommandText = """
            UPDATE outbox_records
            SET status = $processing, claimed_at = $claimedAt, updated_at = $claimedAt
            WHERE id = $id AND status = $pending;
            """;
        command.Parameters.AddWithValue("$processing", OutboxStatus.Processing.ToString());
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$claimedAt", claimedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", outboxId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected == 1;
    }

    public async Task MarkSyncedAsync(long outboxId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox_records
            SET status = $synced, updated_at = $now, last_attempt_at = $now, last_error = NULL
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$synced", OutboxStatus.Synced.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", outboxId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkRetryAsync(long outboxId, DateTimeOffset nextAttemptAt, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            UPDATE outbox_records
            SET status = $pending, attempt_count = attempt_count + 1,
                last_attempt_at = $now, next_attempt_at = $nextAttemptAt, last_error = $error,
                claimed_at = NULL, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAt.ToString("O"));
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", outboxId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(long outboxId, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            UPDATE outbox_records
            SET status = $failed, attempt_count = attempt_count + 1,
                last_attempt_at = $now, last_error = $error, claimed_at = NULL, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$failed", OutboxStatus.Failed.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", outboxId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RecoverStaleProcessingAsync(DateTimeOffset now, TimeSpan staleThreshold, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        var staleBefore = now - staleThreshold;
        command.CommandText = """
            UPDATE outbox_records
            SET status = $pending, claimed_at = NULL, updated_at = $now
            WHERE status = $processing AND (claimed_at IS NULL OR claimed_at <= $staleBefore);
            """;
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$processing", OutboxStatus.Processing.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$staleBefore", staleBefore.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbox_records WHERE status IN ($pending, $processing);";
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$processing", OutboxStatus.Processing.ToString());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    internal static OutboxRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        TransactionLocalId = reader.GetInt64(1),
        LocalIdempotencyKey = reader.GetString(2),
        Status = Enum.Parse<OutboxStatus>(reader.GetString(3)),
        AttemptCount = reader.GetInt32(4),
        LastAttemptAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
        NextAttemptAt = DateTimeOffset.Parse(reader.GetString(6)),
        LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
        ClaimedAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(9)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(10)),
    };
}
