namespace AgentBridge.Abstractions.Models;

/// <summary>
/// The full persisted state of the orchestrator. Serialized atomically to
/// AgentBridgeState.json so the application can recover safely after a restart.
/// This is a plain immutable data record — persistence format, not behavior.
/// </summary>
public sealed record BridgeStateSnapshot
{
    public required BridgeState CurrentState { get; init; }

    /// <summary>The state to return to when Resume is called from Paused. Null unless CurrentState == Paused.</summary>
    public BridgeState? StateBeforePause { get; init; }

    public int CurrentIteration { get; init; }

    public int MaximumIterations { get; init; } = 50;

    public string? LastClaudeReportHash { get; init; }

    public string? LastCodexPromptHash { get; init; }

    public AgentRole? LastAgent { get; init; }

    public string? LastAction { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? LastClaudeReportUpdateUtc { get; init; }

    public DateTimeOffset? LastCodexPromptUpdateUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>Schema version of this snapshot, for forward-compatible migrations.</summary>
    public int SchemaVersion { get; init; } = 1;

    public static BridgeStateSnapshot CreateInitial(int maximumIterations, DateTimeOffset now) => new()
    {
        CurrentState = BridgeState.Idle,
        CurrentIteration = 0,
        MaximumIterations = maximumIterations,
        StartedAtUtc = now,
        UpdatedAtUtc = now,
    };
}
