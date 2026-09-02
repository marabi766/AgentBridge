namespace AgentBridge.Abstractions.Models;

/// <summary>
/// Aggregated, UI-ready read model. This is the single object a future dashboard
/// binds to — it deliberately flattens state + config highlights + git + agent
/// status so the presentation layer never has to reach into orchestration internals.
/// </summary>
public sealed record BridgeStatusView
{
    public required BridgeState CurrentState { get; init; }

    public required string StatusText { get; init; }

    public required int CurrentIteration { get; init; }

    public required int MaximumIterations { get; init; }

    public required AgentStatus ClaudeStatus { get; init; }

    public required AgentStatus CodexStatus { get; init; }

    public required bool IsRunning { get; init; }

    public required bool IsPaused { get; init; }

    public string? LastAction { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? LastClaudeReportUpdateUtc { get; init; }

    public DateTimeOffset? LastCodexPromptUpdateUtc { get; init; }

    public string? GitBranch { get; init; }

    public string? GitWorkingTreeSummary { get; init; }

    public bool DryRun { get; init; }

    public required DateTimeOffset GeneratedAtUtc { get; init; }
}
