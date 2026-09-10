using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Notifications;

/// <summary>
/// Delivers each notification to every configured channel. One channel failing
/// or being slow must not stop the others: the desktop balloon still needs to
/// appear if Telegram is unreachable, and vice versa.
/// </summary>
public sealed class CompositeNotificationService : INotificationService, ISupportsNotificationTest
{
    private readonly IReadOnlyList<INotificationService> _channels;
    private readonly ILogger<CompositeNotificationService> _logger;

    public CompositeNotificationService(
        IEnumerable<INotificationService> channels,
        ILogger<CompositeNotificationService> logger)
    {
        // A composite must not contain itself, however the container wires it up.
        _channels = channels.Where(channel => channel is not CompositeNotificationService).ToList();
        _logger = logger;
    }

    public async Task NotifyAsync(
        string title,
        string message,
        NotificationLevel level,
        CancellationToken cancellationToken,
        NotificationAttachment? attachment = null)
    {
        foreach (var channel in _channels)
        {
            try
            {
                await channel.NotifyAsync(title, message, level, cancellationToken, attachment).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Channel} failed to deliver a notification.", channel.GetType().Name);
            }
        }
    }

    public async Task<bool> SendTestNotificationAsync(CancellationToken cancellationToken)
    {
        var delivered = false;
        foreach (var channel in _channels.OfType<ISupportsNotificationTest>())
        {
            try
            {
                delivered |= await channel.SendTestNotificationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Channel} failed a notification test.", channel.GetType().Name);
            }
        }

        return delivered;
    }
}
