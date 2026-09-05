using CCMC.Application.Abstractions;
using CCMC.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace CCMC.Infrastructure.Persistence.Repositories;

public sealed class AuditLogRepository(SqliteConnectionFactory connectionFactory) : IAuditLogRepository
{
    public async Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_log_entries
                (user_id, centre_id, action, resource_type, resource_id, old_value_json, new_value_json, reason, created_at)
            VALUES
                ($userId, $centreId, $action, $resourceType, $resourceId, $oldValue, $newValue, $reason, $createdAt);
            """;
        command.Parameters.AddWithValue("$userId", (object?)entry.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$centreId", (object?)entry.CentreId ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", entry.Action);
        command.Parameters.AddWithValue("$resourceType", entry.ResourceType);
        command.Parameters.AddWithValue("$resourceId", entry.ResourceId);
        command.Parameters.AddWithValue("$oldValue", (object?)entry.OldValueJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$newValue", (object?)entry.NewValueJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)entry.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> ListRecentAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, centre_id, action, resource_type, resource_id, old_value_json, new_value_json, reason, created_at
            FROM audit_log_entries
            ORDER BY created_at DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", take);

        var results = new List<AuditLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AuditLogEntry
            {
                Id = reader.GetInt64(0),
                UserId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                CentreId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Action = reader.GetString(3),
                ResourceType = reader.GetString(4),
                ResourceId = reader.GetString(5),
                OldValueJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                NewValueJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                Reason = reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(9)),
            });
        }
        return results;
    }
}
