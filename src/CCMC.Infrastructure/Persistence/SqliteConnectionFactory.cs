using Microsoft.Data.Sqlite;

namespace CCMC.Infrastructure.Persistence;

/// <summary>
/// Opens a configured SQLite connection. Short-lived-connection-per-operation
/// pattern (standard for Microsoft.Data.Sqlite, which pools under the hood) -
/// callers `using`/`await using` each connection they open.
///
/// Durability settings applied on every open, since SQLite does not persist
/// PRAGMA settings in the file itself: WAL (well-defined crash recovery - a
/// WAL frame is either fully written and replayed, or ignored, never torn)
/// and synchronous=FULL (fsync every commit rather than trust the OS write
/// cache). Same reasoning as the legacy gateway's documented design
/// (context.md) - this system's actual volume (a handful of receptions per
/// hour per centre) makes the latency cost irrelevant next to "no data loss."
/// </summary>
public sealed class SqliteConnectionFactory(string databasePath)
{
    public string DatabasePath { get; } = databasePath;

    public SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous = FULL;";
            pragma.ExecuteNonQuery();
        }
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }
}
