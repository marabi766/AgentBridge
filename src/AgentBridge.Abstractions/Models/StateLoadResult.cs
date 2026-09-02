namespace AgentBridge.Abstractions.Models;

public enum StateLoadStatus
{
    /// <summary>No persisted state file exists yet — this is a fresh start.</summary>
    NotFound,

    /// <summary>State loaded and deserialized successfully.</summary>
    Loaded,

    /// <summary>A state file exists but could not be parsed/validated. Never auto-resume from this.</summary>
    Corrupted,
}

public sealed record StateLoadResult
{
    public required StateLoadStatus Status { get; init; }

    public BridgeStateSnapshot? Snapshot { get; init; }

    public string? ErrorMessage { get; init; }

    public string? BackupFilePath { get; init; }

    public static StateLoadResult NotFound() => new() { Status = StateLoadStatus.NotFound };

    public static StateLoadResult Loaded(BridgeStateSnapshot snapshot) => new()
    {
        Status = StateLoadStatus.Loaded,
        Snapshot = snapshot,
    };

    public static StateLoadResult Corrupted(string errorMessage, string? backupFilePath) => new()
    {
        Status = StateLoadStatus.Corrupted,
        ErrorMessage = errorMessage,
        BackupFilePath = backupFilePath,
    };
}
