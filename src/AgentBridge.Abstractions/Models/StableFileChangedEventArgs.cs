namespace AgentBridge.Abstractions.Models;

public sealed class StableFileChangedEventArgs : EventArgs
{
    public required string FilePath { get; init; }

    public required string Content { get; init; }

    public required string ContentHashSha256 { get; init; }

    /// <summary>
    /// When the file itself was last written, as opposed to when this watcher
    /// noticed it. The orchestrator needs the difference: content that was already
    /// on disk before an instruction was sent cannot be that instruction's answer,
    /// however recently the watcher happened to read it.
    /// </summary>
    public required DateTimeOffset LastWriteTimeUtc { get; init; }

    public required DateTimeOffset DetectedAtUtc { get; init; }
}

public sealed class FileWatcherErrorEventArgs : EventArgs
{
    public required string FilePath { get; init; }

    public required string Message { get; init; }

    public Exception? Exception { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}
