namespace AgentBridge.Abstractions.Models;

/// <summary>
/// Operator-selected passive point from which a bridge session resumes.
/// Neither option sends immediately; the corresponding protocol watcher waits
/// for the next distinct file revision.
/// </summary>
public enum BridgeStartPoint
{
    WaitForClaudeReport,
    WaitForCodexPrompt,
}
