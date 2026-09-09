using CCMC.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CCMC.Tests.Persistence;

public class SchemaMigratorTests
{
    [Fact]
    public void Migrate_CreatesAllExpectedTables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccmc-schema-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(path);
            new SchemaMigrator(factory).Migrate();

            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var reader = command.ExecuteReader();
            var tables = new HashSet<string>();
            while (reader.Read()) tables.Add(reader.GetString(0));

            foreach (var expected in new[]
                     {
                         "chilling_centres", "sources", "vehicles", "quality_rules", "device_configurations",
                         "local_transactions", "transaction_overrides", "outbox_records", "audit_log_entries",
                         "schema_migrations",
                     })
            {
                Assert.Contains(expected, tables);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Migrate_CalledTwice_IsIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccmc-schema-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(path);
            var migrator = new SchemaMigrator(factory);

            migrator.Migrate();
            var exception = Record.Exception(() => migrator.Migrate());

            Assert.Null(exception);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
