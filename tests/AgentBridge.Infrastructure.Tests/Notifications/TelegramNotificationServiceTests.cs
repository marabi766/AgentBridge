using System.Net;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Notifications;

public sealed class TelegramNotificationServiceTests
{
    private static BridgeConfiguration Configured => new()
    {
        TelegramNotificationsEnabled = true,
        TelegramBotToken = "123:ABC",
        TelegramChatId = "42",
        TelegramIncludeReports = true,
    };

    [Fact]
    public async Task ANotificationBecomesOneSendMessageCallToTheBotApi()
    {
        var handler = new RecordingHandler();
        var service = Build(handler, Configured);

        await service.NotifyAsync("Claude finished iteration 3", "report is attached", NotificationLevel.Info, default);

        var call = Assert.Single(handler.Requests);
        Assert.Equal("/bot123:ABC/sendMessage", call.Path);
        var body = Uri.UnescapeDataString(call.Body.Replace('+', ' '));
        Assert.Contains("Claude finished iteration 3", body);
        Assert.Contains("chat_id=42", body);
    }

    [Fact]
    public async Task TheReportIsSentAsASeparateDocumentWhenOneIsAttached()
    {
        var handler = new RecordingHandler();
        var service = Build(handler, Configured);

        await service.NotifyAsync(
            "Claude finished iteration 3", "done", NotificationLevel.Info, default,
            new NotificationAttachment { FileName = "ClaudeResultReport.md", Content = "# Report\nall good" });

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/sendMessage", handler.Requests[0].Path);
        Assert.EndsWith("/sendDocument", handler.Requests[1].Path);
        Assert.Contains("ClaudeResultReport.md", handler.Requests[1].Body);
    }

    [Fact]
    public async Task TheReportIsNotAttachedWhenTheOperatorTurnedThatOff()
    {
        var handler = new RecordingHandler();
        var service = Build(handler, Configured with { TelegramIncludeReports = false });

        await service.NotifyAsync(
            "x", "y", NotificationLevel.Info, default,
            new NotificationAttachment { FileName = "r.md", Content = "body" });

        Assert.Single(handler.Requests);
        Assert.EndsWith("/sendMessage", handler.Requests[0].Path);
    }

    [Theory]
    [InlineData(false, "123:ABC", "42")]
    [InlineData(true, null, "42")]
    [InlineData(true, "123:ABC", null)]
    public async Task NothingIsSentUntilTheChannelIsEnabledAndBothIdentifiersAreSet(
        bool enabled, string? token, string? chatId)
    {
        var handler = new RecordingHandler();
        var service = Build(handler, new BridgeConfiguration
        {
            TelegramNotificationsEnabled = enabled,
            TelegramBotToken = token,
            TelegramChatId = chatId,
        });

        await service.NotifyAsync("x", "y", NotificationLevel.Info, default);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ATelegramErrorIsSwallowedRatherThanThrown()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, """{"ok":false,"description":"chat not found"}""");
        var service = Build(handler, Configured);

        // Must not throw — a run does not stop because a chat id is wrong.
        await service.NotifyAsync("x", "y", NotificationLevel.Error, default);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ATransportFailureIsSwallowed()
    {
        var service = Build(new ThrowingHandler(), Configured);

        await service.NotifyAsync("x", "y", NotificationLevel.Info, default);
    }

    [Fact]
    public async Task TheTestMessageReportsWhetherTheApiAcceptedIt()
    {
        var ok = Build(new RecordingHandler(), Configured);
        Assert.True(await ((ISupportsNotificationTest)ok).SendTestNotificationAsync(default));

        var bad = Build(new RecordingHandler(HttpStatusCode.Unauthorized, "{}"), Configured);
        Assert.False(await ((ISupportsNotificationTest)bad).SendTestNotificationAsync(default));

        var off = Build(new RecordingHandler(), new BridgeConfiguration());
        Assert.False(await ((ISupportsNotificationTest)off).SendTestNotificationAsync(default));
    }

    [Fact]
    public async Task AMessageOverTelegramsLimitIsTrimmedRatherThanRejected()
    {
        var handler = new RecordingHandler();
        var service = Build(handler, Configured);

        await service.NotifyAsync("t", new string('x', 6000), NotificationLevel.Info, default);

        var call = Assert.Single(handler.Requests);
        // The URL-encoded body is longer than the text, but the text field must
        // be within Telegram's 4096.
        Assert.True(Uri.UnescapeDataString(call.Body).Length < 4200);
    }

    private static TelegramNotificationService Build(HttpMessageHandler handler, BridgeConfiguration configuration) =>
        new(new FixedConfig(configuration), new HttpClient(handler), NullLogger<TelegramNotificationService>.Instance);

    private sealed class FixedConfig(BridgeConfiguration configuration) : IConfigurationService
    {
        public Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(configuration);

        public Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingHandler(
        HttpStatusCode status = HttpStatusCode.OK, string body = """{"ok":true}""") : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, content));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to host");
    }
}
