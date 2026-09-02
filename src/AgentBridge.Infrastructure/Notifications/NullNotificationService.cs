using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Notifications;

/// <summary>
/// No-op notification sink used until the UI phase implements real Windows toast
/// notifications (see FUTURE_UI.md). Still logs so notification-worthy events are
/// visible in the log files during backend-only operation.
/// </summary>
public sealed class NullNotificationService(ILogger<NullNotificationService> logger) : INotificationService
{
    public Task NotifyAsync(string title, string message, NotificationLevel level, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Notification:{Level}] {Title} — {Message}", level, title, message);
        return Task.CompletedTask;
    }
}
