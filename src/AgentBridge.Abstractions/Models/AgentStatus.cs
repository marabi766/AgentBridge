namespace AgentBridge.Abstractions.Models;

/// <summary>
/// Coarse, UI-facing readiness status for a single agent adapter.
/// </summary>
public enum AgentStatus
{
    Unknown,
    NotRunning,
    Launching,
    Running,
    Ready,
    Busy,
    Unreachable,
    Error,
}
