using CCMC.Infrastructure.Persistence;

namespace CCMC.Tests.Persistence;

/// <summary>Each test gets its own temp-file SQLite database, migrated fresh, deleted on dispose.</summary>
public sealed class SqliteTestFixture : IDisposable
{
    public SqliteConnectionFactory ConnectionFactory { get; }
    private readonly string _path;

    public SqliteTestFixture()
    {
        _path = Path.Combine(Path.GetTempPath(), $"ccmc-test-{Guid.NewGuid():N}.db");
        ConnectionFactory = new SqliteConnectionFactory(_path);
        new SchemaMigrator(ConnectionFactory).Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            foreach (var extra in new[] { _path + "-wal", _path + "-shm" })
            {
                if (File.Exists(extra)) File.Delete(extra);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only - a lingering temp file does not fail the test.
        }
    }
}
