namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// A notification channel that can be probed on demand, so the operator can
/// confirm it is configured correctly without waiting for a real event.
/// </summary>
public interface ISupportsNotificationTest
{
    /// <summary>
    /// Sends a test message. Returns true only if the channel actually delivered
    /// it — a disabled or misconfigured channel returns false rather than throwing.
    /// </summary>
    Task<bool> SendTestNotificationAsync(CancellationToken cancellationToken);
}
