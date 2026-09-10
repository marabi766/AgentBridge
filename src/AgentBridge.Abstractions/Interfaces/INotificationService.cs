using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

public interface INotificationService
{
    /// <summary>
    /// Delivers one notification. <paramref name="attachment"/>, when given, is a
    /// document the channel may send alongside the text (the protocol file an
    /// agent just produced); a channel that cannot carry a file ignores it.
    /// Implementations must not throw for a delivery that simply failed.
    /// </summary>
    Task NotifyAsync(
        string title,
        string message,
        NotificationLevel level,
        CancellationToken cancellationToken,
        NotificationAttachment? attachment = null);
}
