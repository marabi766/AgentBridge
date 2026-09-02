namespace AgentBridge.Infrastructure.Paths;

/// <summary>
/// Resolves where Agent Bridge stores its own application data. Deliberately kept
/// separate from both the Agent Bridge source repository and whatever target
/// project repository the bridge is orchestrating, so the bridge never writes
/// internal files into a user's git repo.
/// </summary>
public static class AppPaths
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge");

    public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public static string StateFilePath => Path.Combine(RootDirectory, "AgentBridgeState.json");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
