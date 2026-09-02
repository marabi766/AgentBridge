using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.StateMachine;

/// <summary>
/// Maps each fine-grained state machine state to the unambiguous status text a UI
/// should display. Deliberately avoids vague labels like "Running".
/// </summary>
public static class BridgeStateDescriptions
{
    public static string Describe(BridgeState state) => state switch
    {
        BridgeState.Idle => "Idle",
        BridgeState.WaitingForClaudeReport => "Waiting for Claude",
        BridgeState.ClaudeReportDetected => "Claude report detected",
        BridgeState.WaitingForCodex => "Waiting for Codex",
        BridgeState.CodexProcessing => "Codex processing",
        BridgeState.WaitingForCodexPrompt => "Waiting for Codex",
        BridgeState.CodexPromptDetected => "Codex prompt detected",
        BridgeState.WaitingForClaude => "Waiting for Claude",
        BridgeState.ClaudeProcessing => "Claude processing",
        BridgeState.Paused => "Paused",
        BridgeState.Stopped => "Stopped",
        BridgeState.Error => "Error",
        _ => state.ToString(),
    };
}
