using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// Uniform contract for driving one GUI agent application (Claude Desktop, ChatGPT
/// Desktop/Codex, or a test double). Implementations must not throw for expected
/// negative outcomes (app not running, element not found) — they return false/Unknown
/// and let the caller decide policy (retry, error, notify). Only truly unexpected
/// failures should throw.
/// </summary>
public interface IAgentAdapter
{
    string Name { get; }

    AgentRole Role { get; }

    Task<bool> IsApplicationRunningAsync(CancellationToken cancellationToken);

    Task<bool> LaunchApplicationAsync(CancellationToken cancellationToken);

    Task<bool> IsReadyAsync(CancellationToken cancellationToken);

    Task<bool> ActivateAsync(CancellationToken cancellationToken);

    Task<bool> FindConversationAsync(CancellationToken cancellationToken);

    Task<bool> FindInputBoxAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends the message and, where the underlying UI exposes a way to verify it,
    /// confirms the message was actually submitted (e.g. input box cleared, a new
    /// message element appeared). Returns false if delivery could not be verified.
    /// </summary>
    Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken);

    Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken);
}
