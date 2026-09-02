using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// The single control surface a future UI (or a test harness) uses to drive the
/// bridge. No orchestration logic belongs in a caller of this interface — it all
/// lives behind it.
/// </summary>
public interface IOrchestratorService
{
    Task StartAsync(CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task<bool> TestClaudeConnectionAsync(CancellationToken cancellationToken);

    Task<bool> TestCodexConnectionAsync(CancellationToken cancellationToken);

    Task<BridgeStatusView> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Resets a corrupted or ambiguous persisted state back to a fresh Idle start. Never invoked automatically.</summary>
    Task ResetStateAsync(CancellationToken cancellationToken);

    event EventHandler<BridgeStatusView>? StatusChanged;
}
