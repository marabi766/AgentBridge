using System.Net.Http.Json;
using System.Text;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Notifications;

/// <summary>
/// Delivers notifications to a Telegram chat through the Bot API.
///
/// The settings are read on each call rather than captured once, so a token or
/// chat id corrected in Settings takes effect without a restart — the same
/// reasoning as the command line adapters. Every failure is swallowed and
/// logged: a chat that cannot be reached must never stop a run or hold up the
/// desktop notification beside it.
///
/// The <see cref="HttpClient"/> is the default one, which reads the
/// <c>HTTPS_PROXY</c> environment variable, so it works on a machine whose only
/// route out is a local proxy.
/// </summary>
public sealed class TelegramNotificationService : INotificationService, ISupportsNotificationTest
{
    // Telegram rejects a message body over 4096 characters outright.
    private const int MaxMessageLength = 4096;

    private readonly IConfigurationService _configurationService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(
        IConfigurationService configurationService,
        HttpClient httpClient,
        ILogger<TelegramNotificationService> logger)
    {
        _configurationService = configurationService;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task NotifyAsync(
        string title,
        string message,
        NotificationLevel level,
        CancellationToken cancellationToken,
        NotificationAttachment? attachment = null)
    {
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return;
        }

        var (token, chatId, includeReports) = settings.Value;
        var marker = level switch
        {
            NotificationLevel.Error => "❌",
            NotificationLevel.Warning => "⚠️",
            _ => "ℹ️",
        };

        var body = $"{marker} {title}\n\n{message}";
        var sent = await SendMessageAsync(token, chatId, body, cancellationToken).ConfigureAwait(false);

        if (sent && includeReports && attachment is not null && attachment.Content.Length > 0)
        {
            await SendDocumentAsync(token, chatId, attachment, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> SendTestNotificationAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            _logger.LogWarning(
                "Telegram test skipped: notifications are off, or the bot token or chat id is missing.");
            return false;
        }

        var (token, chatId, _) = settings.Value;
        return await SendMessageAsync(
            token,
            chatId,
            "✅ Agent Bridge is connected to this chat. You will get a message here on every "
            + "iteration, and when a run needs attention.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string Token, string ChatId, bool IncludeReports)?> ReadSettingsAsync(
        CancellationToken cancellationToken)
    {
        BridgeConfiguration configuration;
        try
        {
            configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read settings for a Telegram notification.");
            return null;
        }

        if (!configuration.TelegramNotificationsEnabled
            || string.IsNullOrWhiteSpace(configuration.TelegramBotToken)
            || string.IsNullOrWhiteSpace(configuration.TelegramChatId))
        {
            return null;
        }

        return (configuration.TelegramBotToken.Trim(),
            configuration.TelegramChatId.Trim(),
            configuration.TelegramIncludeReports);
    }

    private async Task<bool> SendMessageAsync(
        string token, string chatId, string text, CancellationToken cancellationToken)
    {
        var trimmed = text.Length <= MaxMessageLength
            ? text
            : text[..(MaxMessageLength - 1)] + "…";

        var payload = new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = trimmed,
            ["disable_web_page_preview"] = "true",
        };

        return await CallAsync(token, "sendMessage", new FormUrlEncodedContent(payload), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> SendDocumentAsync(
        string token, string chatId, NotificationAttachment attachment, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chatId), "chat_id" },
        };

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(attachment.Content));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/markdown");
        form.Add(file, "document", SafeFileName(attachment.FileName));

        return await CallAsync(token, "sendDocument", form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CallAsync(
        string token, string method, HttpContent content, CancellationToken cancellationToken)
    {
        var url = $"https://api.telegram.org/bot{token}/{method}";
        try
        {
            using var response = await _httpClient
                .PostAsync(url, content, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var detail = await ReadDescriptionAsync(response, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Telegram {Method} returned {Status}: {Detail}", method, (int)response.StatusCode, detail);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Telegram {Method} failed.", method);
            return false;
        }
    }

    private static async Task<string> ReadDescriptionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync<TelegramError>(cancellationToken)
                .ConfigureAwait(false);
            return error?.Description ?? "(no description)";
        }
        catch (Exception)
        {
            return "(unparseable response)";
        }
    }

    private static string SafeFileName(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "report.md" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        return trimmed;
    }

    private sealed record TelegramError
    {
        [System.Text.Json.Serialization.JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
