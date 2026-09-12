using CCMC.Infrastructure.Persistence.Migrations;

namespace CCMC.Infrastructure.Persistence;

/// <summary>
/// A real, ordered, tracked migration runner - not a single ad-hoc
/// "CREATE TABLE IF NOT EXISTS" blob re-run on every start. schema_migrations
/// records what has been applied; each migration runs inside its own
/// transaction so a failed migration never leaves the schema half-applied.
/// Design adapted from the legacy gateway's proven approach (context.md).
/// </summary>
public sealed class SchemaMigrator(SqliteConnectionFactory connectionFactory)
{
    private static readonly IReadOnlyList<(int Version, string Name, string Sql)> Migrations =
    [
        (Migration001InitialSchema.Version, Migration001InitialSchema.Name, Migration001InitialSchema.Sql),
        (Migration002OfflineAndOverrideSync.Version, Migration002OfflineAndOverrideSync.Name, Migration002OfflineAndOverrideSync.Sql),
        (Migration003AnalyserFields.Version, Migration003AnalyserFields.Name, Migration003AnalyserFields.Sql),
    ];

    public void Migrate()
    {
        using var connection = connectionFactory.Open();

        using (var createTracking = connection.CreateCommand())
        {
            createTracking.CommandText =
                "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);";
            createTracking.ExecuteNonQuery();
        }

        var applied = new HashSet<int>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT version FROM schema_migrations;";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var migration in Migrations.OrderBy(m => m.Version))
        {
            if (applied.Contains(migration.Version)) continue;

            using var transaction = connection.BeginTransaction();
            try
            {
                using (var apply = connection.CreateCommand())
                {
                    apply.Transaction = transaction;
                    apply.CommandText = migration.Sql;
                    apply.ExecuteNonQuery();
                }

                using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText =
                        "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($version, $name, $appliedAt);";
                    record.Parameters.AddWithValue("$version", migration.Version);
                    record.Parameters.AddWithValue("$name", migration.Name);
                    record.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                    record.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}
