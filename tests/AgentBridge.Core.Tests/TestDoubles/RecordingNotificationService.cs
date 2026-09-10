using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

public sealed class RecordingNotificationService : INotificationService
{
    public List<(string Title, string Message, NotificationLevel Level, NotificationAttachment? Attachment)> Notifications { get; } = [];

    public Task NotifyAsync(
        string title,
        string message,
        NotificationLevel level,
        CancellationToken cancellationToken,
        NotificationAttachment? attachment = null)
    {
        Notifications.Add((title, message, level, attachment));
        return Task.CompletedTask;
    }
}
