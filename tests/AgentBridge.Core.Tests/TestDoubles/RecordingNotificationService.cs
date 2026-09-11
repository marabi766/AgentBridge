using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

public sealed class RecordingNotificationService : INotificationService
{
    public List<(string Title, string Message, NotificationLevel Level, NotificationAttachment? Attachment)> Notifications { get; } = [];

    /// <summary>
    /// How many calls arrived already cancelled. The real desktop notifier
    /// checks this and silently returns without showing anything — correct
    /// there, but it means a caller who hands over a token some earlier step
    /// already cancelled gets no toast and no error either, only a quiet
    /// nothing. Recording it here rather than throwing lets a test tell "no
    /// notification was warranted" from "one was owed and got lost".
    /// </summary>
    public int AlreadyCancelledCallCount { get; private set; }

    public Task NotifyAsync(
        string title,
        string message,
        NotificationLevel level,
        CancellationToken cancellationToken,
        NotificationAttachment? attachment = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            AlreadyCancelledCallCount++;
        }

        Notifications.Add((title, message, level, attachment));
        return Task.CompletedTask;
    }
}
