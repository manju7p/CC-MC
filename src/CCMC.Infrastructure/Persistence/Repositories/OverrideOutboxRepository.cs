using CCMC.Application.Abstractions;
using CCMC.Domain.Enums;
using CCMC.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace CCMC.Infrastructure.Persistence.Repositories;

/// <summary>
/// Same state-machine mechanics as OutboxRepository (atomic conditional-UPDATE
/// claim, retry/failed/synced transitions, stale-PROCESSING recovery) - see
/// that class for the reasoning, not repeated here. The one structural
/// difference: GetEligibleAsync joins local_transactions so a row is only
/// ever returned once its parent reception has actually synced (has a
/// non-null cloud_transaction_id) - an override cannot be pushed to the
/// cloud before the reception it targets exists there.
/// </summary>
public sealed class OverrideOutboxRepository(SqliteConnectionFactory connectionFactory) : IOverrideOutboxRepository
{
    private const string SelectColumns = """
        SELECT oo.id, oo.override_id, oo.transaction_local_id, oo.new_status, oo.reason, oo.status,
               oo.attempt_count, oo.last_attempt_at, oo.next_attempt_at, oo.last_error, oo.claimed_at,
               oo.created_at, oo.updated_at
        FROM override_outbox oo
        """;

    public async Task<IReadOnlyList<OverrideOutboxRecord>> GetEligibleAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + """

            JOIN local_transactions lt ON lt.local_id = oo.transaction_local_id
            WHERE oo.status = $pending AND oo.next_attempt_at <= $now AND lt.cloud_transaction_id IS NOT NULL
            ORDER BY oo.next_attempt_at ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<OverrideOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }
        return results;
    }

    public async Task<bool> TryClaimAsync(long id, DateTimeOffset claimedAt, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE override_outbox
            SET status = $processing, claimed_at = $claimedAt, updated_at = $claimedAt
            WHERE id = $id AND status = $pending;
            """;
        command.Parameters.AddWithValue("$processing", OutboxStatus.Processing.ToString());
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$claimedAt", claimedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task MarkSyncedAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE override_outbox
            SET status = $synced, updated_at = $now, last_attempt_at = $now, last_error = NULL
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$synced", OutboxStatus.Synced.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            UPDATE override_outbox
            SET status = $pending, attempt_count = attempt_count + 1,
                last_attempt_at = $now, next_attempt_at = $nextAttemptAt, last_error = $error,
                claimed_at = NULL, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAt.ToString("O"));
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            UPDATE override_outbox
            SET status = $failed, attempt_count = attempt_count + 1,
                last_attempt_at = $now, last_error = $error, claimed_at = NULL, updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$failed", OutboxStatus.Failed.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RecoverStaleProcessingAsync(DateTimeOffset now, TimeSpan staleThreshold, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        var staleBefore = now - staleThreshold;
        command.CommandText = """
            UPDATE override_outbox
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
        command.CommandText = "SELECT COUNT(*) FROM override_outbox WHERE status IN ($pending, $processing);";
        command.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        command.Parameters.AddWithValue("$processing", OutboxStatus.Processing.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static OverrideOutboxRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        OverrideId = reader.GetInt64(1),
        TransactionLocalId = reader.GetInt64(2),
        NewStatus = Enum.Parse<TransactionStatus>(reader.GetString(3)),
        Reason = reader.GetString(4),
        Status = Enum.Parse<OutboxStatus>(reader.GetString(5)),
        AttemptCount = reader.GetInt32(6),
        LastAttemptAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        NextAttemptAt = DateTimeOffset.Parse(reader.GetString(8)),
        LastError = reader.IsDBNull(9) ? null : reader.GetString(9),
        ClaimedAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(11)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(12)),
    };
}
