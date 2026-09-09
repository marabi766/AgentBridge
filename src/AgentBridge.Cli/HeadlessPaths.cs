namespace AgentBridge.Cli;

/// <summary>
/// Where the headless host keeps its own settings, state and logs.
///
/// Deliberately separate from the desktop application's files. The two hosts
/// drive the same project with different transports and usually different
/// safety settings, and sharing one settings file would mean starting the
/// console host silently rewrote what the window shows — or worse, that a live
/// console run inherited a dry-run flag nobody looked at.
/// </summary>
public static class HeadlessPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge", "cli");

    public static string DefaultSettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
