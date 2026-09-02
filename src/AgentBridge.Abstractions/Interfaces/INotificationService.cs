using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

public interface INotificationService
{
    Task NotifyAsync(string title, string message, NotificationLevel level, CancellationToken cancellationToken);
}
