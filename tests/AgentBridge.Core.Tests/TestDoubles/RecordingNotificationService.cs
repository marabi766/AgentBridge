using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

public sealed class RecordingNotificationService : INotificationService
{
    public List<(string Title, string Message, NotificationLevel Level)> Notifications { get; } = [];

    public Task NotifyAsync(string title, string message, NotificationLevel level, CancellationToken cancellationToken)
    {
        Notifications.Add((title, message, level));
        return Task.CompletedTask;
    }
}
