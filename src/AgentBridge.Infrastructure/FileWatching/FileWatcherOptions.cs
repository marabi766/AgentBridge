namespace AgentBridge.Infrastructure.FileWatching;

public sealed record FileWatcherOptions
{
    public int DebounceMilliseconds { get; init; } = 400;

    public int StabilityCheckIntervalMilliseconds { get; init; } = 300;

    public int RequiredConsecutiveStableChecks { get; init; } = 3;

    public int ReadRetryCount { get; init; } = 5;

    public int ReadRetryDelayMilliseconds { get; init; } = 200;
}
