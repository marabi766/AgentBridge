namespace AgentBridge.Abstractions.Models;

/// <summary>
/// Explicit states of the Agent Bridge orchestration state machine.
/// Transitions are enforced by <c>AgentBridge.Core.StateMachine.BridgeStateMachine</c>.
/// </summary>
public enum BridgeState
{
    Idle,
    WaitingForClaudeReport,
    ClaudeReportDetected,
    WaitingForCodex,
    CodexProcessing,
    WaitingForCodexPrompt,
    CodexPromptDetected,
    WaitingForClaude,
    ClaudeProcessing,
    Paused,
    Stopped,
    Error,
}
