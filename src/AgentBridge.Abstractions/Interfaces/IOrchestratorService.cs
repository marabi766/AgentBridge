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

    /// <summary>Starts from an explicitly selected passive protocol checkpoint.</summary>
    Task StartAtAsync(BridgeStartPoint startPoint, CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    /// <summary>Resends the current Codex instruction to Claude without advancing or resetting the cycle.</summary>
    Task RetryClaudeDeliveryAsync(CancellationToken cancellationToken);

    /// <summary>Resends the current Claude report instruction to Codex without advancing the cycle.</summary>
    Task RetryCodexDeliveryAsync(CancellationToken cancellationToken);

    /// <summary>Returns a timed-out Claude delivery to report-waiting after the operator verifies it appeared.</summary>
    Task ContinueWaitingForClaudeAsync(CancellationToken cancellationToken);

    /// <summary>Returns a timed-out Codex delivery to prompt-waiting after the operator verifies it appeared.</summary>
    Task ContinueWaitingForCodexAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task<bool> TestClaudeConnectionAsync(CancellationToken cancellationToken);

    Task<bool> TestCodexConnectionAsync(CancellationToken cancellationToken);

    Task<BridgeStatusView> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Resets a corrupted or ambiguous persisted state back to a fresh Idle start. Never invoked automatically.</summary>
    Task ResetStateAsync(CancellationToken cancellationToken);

    event EventHandler<BridgeStatusView>? StatusChanged;
}
