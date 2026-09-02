namespace AgentBridge.Abstractions.Models;

public sealed class StableFileChangedEventArgs : EventArgs
{
    public required string FilePath { get; init; }

    public required string Content { get; init; }

    public required string ContentHashSha256 { get; init; }

    public required DateTimeOffset DetectedAtUtc { get; init; }
}

public sealed class FileWatcherErrorEventArgs : EventArgs
{
    public required string FilePath { get; init; }

    public required string Message { get; init; }

    public Exception? Exception { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}
