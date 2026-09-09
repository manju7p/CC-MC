using System.IO;

namespace CCMC.Desktop.Composition;

/// <summary>
/// Local, per-machine data locations - never mixed with the app's own
/// install directory, so an upgrade never touches local data (same reasoning
/// as the legacy gateway's documented deployment model, context.md).
/// </summary>
public static class AppPaths
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCMC");

    public static string DatabasePath => Path.Combine(RootDirectory, "ccmc.db");
    public static string CapturesDirectory => Path.Combine(RootDirectory, "captures");
    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
}
