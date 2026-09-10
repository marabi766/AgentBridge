using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Notifications;

public sealed class CompositeNotificationServiceTests
{
    [Fact]
    public async Task EveryChannelGetsTheNotification()
    {
        var a = new SpyChannel();
        var b = new SpyChannel();
        var composite = new CompositeNotificationService([a, b], NullLogger<CompositeNotificationService>.Instance);

        await composite.NotifyAsync("t", "m", NotificationLevel.Info, default);

        Assert.Equal(1, a.Count);
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public async Task OneChannelThrowingDoesNotStopTheOthers()
    {
        var failing = new SpyChannel { Throw = true };
        var working = new SpyChannel();
        var composite = new CompositeNotificationService(
            [failing, working], NullLogger<CompositeNotificationService>.Instance);

        // The desktop balloon still has to appear when Telegram is unreachable.
        await composite.NotifyAsync("t", "m", NotificationLevel.Warning, default);

        Assert.Equal(1, working.Count);
    }

    [Fact]
    public async Task TheTestProbeSucceedsIfAnyTestableChannelDelivers()
    {
        var plain = new SpyChannel();
        var testable = new SpyTestableChannel { TestResult = true };
        var composite = new CompositeNotificationService(
            [plain, testable], NullLogger<CompositeNotificationService>.Instance);

        Assert.True(await composite.SendTestNotificationAsync(default));
    }

    [Fact]
    public async Task TheTestProbeFailsWhenNoChannelCanBeTested()
    {
        var composite = new CompositeNotificationService(
            [new SpyChannel()], NullLogger<CompositeNotificationService>.Instance);

        Assert.False(await composite.SendTestNotificationAsync(default));
    }

    private class SpyChannel : INotificationService
    {
        public int Count { get; private set; }

        public bool Throw { get; init; }

        public Task NotifyAsync(
            string title, string message, NotificationLevel level,
            CancellationToken cancellationToken, NotificationAttachment? attachment = null)
        {
            if (Throw)
            {
                throw new InvalidOperationException("channel down");
            }

            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class SpyTestableChannel : SpyChannel, ISupportsNotificationTest
    {
        public bool TestResult { get; init; }

        public Task<bool> SendTestNotificationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TestResult);
    }
}
