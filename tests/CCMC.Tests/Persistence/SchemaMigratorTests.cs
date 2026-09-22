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
                         "rate_formula_settings", "schema_migrations",
                     })
            {
                Assert.Contains(expected, tables);
            }

            // BRD v5.0 section 25: rate/amount columns exist on local_transactions from a fresh create.
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(local_transactions);";
            using var pragmaReader = pragma.ExecuteReader();
            var columns = new HashSet<string>();
            while (pragmaReader.Read()) columns.Add(pragmaReader.GetString(1));
            Assert.Contains("rate", columns);
            Assert.Contains("amount", columns);
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

    /// <summary>
    /// BRD v5.0 section 25 upgrade path: a database created BEFORE
    /// Migration004RateCalculation existed (simulated here by running only
    /// migrations 1-3 directly) must upgrade cleanly when the full migrator
    /// runs, without losing the data already in local_transactions.
    /// </summary>
    [Fact]
    public void Migrate_UpgradingFromPreRateCalculationSchema_AddsColumnsWithoutLosingExistingData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccmc-schema-upgrade-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(path);

            // Simulate a database that only ever ran migrations 1-3 (pre-existing,
            // before this feature) by hand-creating the pre-v4 shape of
            // local_transactions directly (SchemaMigrator's internal Migration00N
            // classes aren't accessible from this test project - this reproduces
            // their net effect on the one table this migration actually touches)
            // and pre-recording versions 1-3 as already applied.
            using (var connection = factory.Open())
            {
                using (var create = connection.CreateCommand())
                {
                    create.CommandText = "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);";
                    create.ExecuteNonQuery();
                }
                using (var createTransactions = connection.CreateCommand())
                {
                    createTransactions.CommandText = """
                        CREATE TABLE local_transactions (
                            local_id INTEGER PRIMARY KEY AUTOINCREMENT,
                            transaction_number TEXT,
                            centre_id INTEGER NOT NULL,
                            source_id INTEGER NOT NULL,
                            vehicle_id INTEGER NOT NULL,
                            operator_user_id INTEGER NOT NULL,
                            quantity_kg REAL NOT NULL,
                            fat REAL NOT NULL,
                            snf REAL NOT NULL,
                            temperature REAL NOT NULL,
                            status TEXT NOT NULL,
                            reading_source TEXT NOT NULL,
                            reason TEXT,
                            local_idempotency_key TEXT NOT NULL UNIQUE,
                            captured_at TEXT NOT NULL,
                            created_at TEXT NOT NULL,
                            cloud_transaction_id INTEGER,
                            clr NUMERIC,
                            water NUMERIC,
                            protein NUMERIC,
                            raw_analyser_payload TEXT
                        );
                        """;
                    createTransactions.ExecuteNonQuery();
                }
                foreach (var version in new[] { 1, 2, 3 })
                {
                    using var record = connection.CreateCommand();
                    record.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($v, $n, $a);";
                    record.Parameters.AddWithValue("$v", version);
                    record.Parameters.AddWithValue("$n", $"PreExisting{version}");
                    record.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("O"));
                    record.ExecuteNonQuery();
                }

                // A pre-existing reception row, saved before Rate Calculation existed.
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO local_transactions
                        (centre_id, source_id, vehicle_id, operator_user_id, quantity_kg, fat, snf, temperature,
                         status, reading_source, local_idempotency_key, captured_at, created_at)
                    VALUES (1, 1, 1, 1, 45.5, 4.5, 9.0, 5.0, 'Accepted', 'Manual', 'pre-existing-key', $now, $now);
                    """;
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }

            // Now run the real, full migrator - this is the "app upgraded" moment.
            new SchemaMigrator(factory).Migrate();

            using (var connection = factory.Open())
            {
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT quantity_kg, rate, amount FROM local_transactions WHERE local_idempotency_key = 'pre-existing-key';";
                using var reader = select.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(45.5, reader.GetDouble(0)); // existing data untouched
                Assert.True(reader.IsDBNull(1)); // rate: NULL, not fabricated, for a row that predates the feature
                Assert.True(reader.IsDBNull(2)); // amount: same
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
