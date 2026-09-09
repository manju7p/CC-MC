using Microsoft.Extensions.Logging;

namespace CCMC.Infrastructure.Logging;

/// <summary>
/// A minimal, dependency-free ILoggerProvider that appends structured lines
/// to a rolling daily file under the given directory (AppPaths.LogsDirectory
/// in production). No external logging package is added, consistent with
/// this project's existing preference for small, hand-rolled infrastructure
/// over extra NuGet dependencies (see SchemaMigrator/SqliteConnectionFactory
/// for the same reasoning applied to persistence).
///
/// Log category (the ILogger&lt;T&gt; type name, e.g. "CCMC.Infrastructure.Devices.DeviceManager")
/// doubles as BRD v2 section 19's Application/Device/Serial/Parser/Reception/
/// Synchronization/Security grouping - each of those concerns lives in its
/// own class today, so the category name already carries that information
/// without a separate taxonomy to keep in sync.
///
/// Every write is a single, complete, tab-separated line - never partial -
/// so a log file being tailed or opened mid-write is never corrupted;
/// concurrent writers are serialized with a simple lock (this app's actual
/// log volume - a handful of events per reception/sync tick - makes a more
/// elaborate async/buffered writer unnecessary).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(string logDirectory, LogLevel minimumLevel = LogLevel.Information)
    {
        Directory.CreateDirectory(logDirectory);
        _filePath = Path.Combine(logDirectory, $"ccmc-{DateTime.UtcNow:yyyyMMdd}.log");
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _filePath, _writeLock, _minimumLevel);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category, string filePath, object writeLock, LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.UtcNow:O}\t{logLevel}\t{category}\t{message}";
            if (exception is not null)
            {
                line += $"\t{exception.GetType().Name}: {exception.Message}";
            }

            lock (writeLock)
            {
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
        }
    }
}
