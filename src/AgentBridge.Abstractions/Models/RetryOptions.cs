namespace AgentBridge.Abstractions.Models;

public sealed record RetryOptions
{
    public int MaxRetries { get; init; } = 3;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(8);

    public double BackoffMultiplier { get; init; } = 2.0;

    public static RetryOptions FromConfiguration(BridgeConfiguration config) => new()
    {
        MaxRetries = config.RetryCount,
        InitialDelay = TimeSpan.FromMilliseconds(config.RetryInitialDelayMilliseconds),
        MaxDelay = TimeSpan.FromMilliseconds(config.RetryMaxDelayMilliseconds),
    };
}
