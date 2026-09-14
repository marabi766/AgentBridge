using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Notifications;

/// <summary>
/// Polls the Telegram Bot API for commands and acts on them through
/// <see cref="IOrchestratorService"/>, the same surface the dashboard drives.
///
/// This turns a bot that only reports into one that also controls the bridge,
/// which is a materially bigger thing to expose, so two separate guards apply
/// before anything beyond <c>/status</c> is acted on: <see cref="BridgeConfiguration.TelegramCommandsEnabled"/>
/// must be on, and the message must come from exactly the configured
/// <see cref="BridgeConfiguration.TelegramChatId"/> — every other chat is
/// ignored without a reply, so a bot whose token leaked does not even confirm
/// that commands exist to try. The one exception is
/// <see cref="BridgeConfiguration.TelegramStatusOnlyChatId"/>, a second,
/// deliberately configured chat that gets <c>/status</c> alone, regardless of
/// <see cref="BridgeConfiguration.TelegramCommandsEnabled"/>.
///
/// Runs as a hosted background service for the process's whole lifetime, not
/// only while a run is active: <c>/run</c> has to reach an idle bridge too.
/// Settings are read fresh on every poll, matching every other Telegram and
/// CLI adapter in this codebase, so turning commands on or off takes effect
/// without a restart.
/// </summary>
public sealed class TelegramCommandListener : BackgroundService
{
    // Telegram holds an update for the bot to collect for a while, so long
    // polling waits for one rather than the loop spinning on empty responses.
    // Comfortably inside the HttpClient's own timeout so a slow reply is
    // reported as "no update" rather than as a fault every cycle.
    private const int LongPollSeconds = 20;

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(15);

    private readonly IConfigurationService _configurationService;
    private readonly IOrchestratorService _orchestrator;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramCommandListener> _logger;

    private long _offset;

    public TelegramCommandListener(
        IConfigurationService configurationService,
        IOrchestratorService orchestrator,
        HttpClient httpClient,
        ILogger<TelegramCommandListener> logger)
    {
        _configurationService = configurationService;
        _orchestrator = orchestrator;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Never lets an exception leave this method. <c>BackgroundService</c>'s
    /// default failure behaviour under <c>Host.CreateApplicationBuilder</c> is
    /// to stop the whole host when a hosted service's <c>ExecuteAsync</c>
    /// faults — an unhandled exception here would take the entire application
    /// down over a Telegram hiccup, not just this listener.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var configuration = await _configurationService.LoadAsync(stoppingToken).ConfigureAwait(false);
                var hasFullControlChat = configuration.TelegramCommandsEnabled
                    && !string.IsNullOrWhiteSpace(configuration.TelegramChatId);
                var hasStatusOnlyChat = !string.IsNullOrWhiteSpace(configuration.TelegramStatusOnlyChatId);
                if (string.IsNullOrWhiteSpace(configuration.TelegramBotToken)
                    || (!hasFullControlChat && !hasStatusOnlyChat))
                {
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await PollOnceAsync(configuration, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram command polling failed; retrying shortly.");
                try
                {
                    await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task PollOnceAsync(BridgeConfiguration configuration, CancellationToken cancellationToken)
    {
        var token = configuration.TelegramBotToken!.Trim();
        var fullControlChatId = configuration.TelegramCommandsEnabled
            ? configuration.TelegramChatId?.Trim()
            : null;
        var statusOnlyChatIds = ParseChatIds(configuration.TelegramStatusOnlyChatId);

        var url = $"https://api.telegram.org/bot{token}/getUpdates"
            + $"?offset={_offset}&timeout={LongPollSeconds}&allowed_updates=%5B%22message%22%5D";

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telegram getUpdates returned {Status}.", (int)response.StatusCode);
            return;
        }

        var body = await response.Content
            .ReadFromJsonAsync<TelegramUpdatesResponse>(cancellationToken)
            .ConfigureAwait(false);
        if (body?.Result is not { Count: > 0 } updates)
        {
            return;
        }

        foreach (var update in updates)
        {
            // Advanced unconditionally and first: an update must never be seen
            // twice, whether or not anything below acts on it. Re-delivering a
            // command because a reply failed to send would fire it again.
            _offset = update.UpdateId + 1;

            var message = update.Message;
            if (message?.Text is null || message.Chat is null)
            {
                continue;
            }

            var fromChatId = message.Chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var isFullControl = fullControlChatId is not null
                && string.Equals(fromChatId, fullControlChatId, StringComparison.Ordinal);
            var isStatusOnly = !isFullControl && statusOnlyChatIds.Contains(fromChatId);
            if (!isFullControl && !isStatusOnly)
            {
                _logger.LogWarning(
                    "Ignored a Telegram message from chat {Chat}, which is not a configured chat.", fromChatId);
                continue;
            }

            await HandleMessageAsync(message.Text, token, fromChatId, isFullControl, configuration, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(
        string text, string token, string chatId, bool isFullControl, BridgeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var command = TelegramCommandParser.Parse(text);
        if (command is null)
        {
            // Ordinary chat, not a command. Nothing to do and nothing to say —
            // replying to every message sent to the bot would be noisy for an
            // operator who also uses it to talk to Claude via Remote Control.
            return;
        }

        var language = TelegramText.ParseLanguage(configuration.TelegramLanguage);

        // The status-only chat gets exactly one command. Answering rather than
        // ignoring is deliberate: unlike an unrecognised chat, this one was
        // configured on purpose, so there is nothing to avoid confirming to it.
        if (!isFullControl && command != TelegramCommand.Status)
        {
            await SendReplyAsync(
                    token, chatId,
                    TelegramMessageFormatting.Prefixed(configuration, TelegramText.StatusOnlyRestriction(language)),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string reply;
        try
        {
            reply = command.Value switch
            {
                TelegramCommand.Help => TelegramText.HelpText(language),
                TelegramCommand.Run => await ExecuteAndConfirmAsync(_orchestrator.StartAsync, TelegramText.Started(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.Stop => await ExecuteAndConfirmAsync(_orchestrator.StopAsync, TelegramText.Stopped(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.Pause => await ExecuteAndConfirmAsync(_orchestrator.PauseAsync, TelegramText.Paused(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.Resume => await ExecuteAndConfirmAsync(_orchestrator.ResumeAsync, TelegramText.Resumed(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.Status => await StatusTextAsync(language, cancellationToken).ConfigureAwait(false),
                TelegramCommand.RetryClaude => await ExecuteAndConfirmAsync(_orchestrator.RetryClaudeDeliveryAsync, TelegramText.RetryingClaude(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.RetryCodex => await ExecuteAndConfirmAsync(_orchestrator.RetryCodexDeliveryAsync, TelegramText.RetryingCodex(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.ContinueClaude => await ExecuteAndConfirmAsync(_orchestrator.ContinueClaudeAsync, TelegramText.ContinuingClaude(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.ContinueCodex => await ExecuteAndConfirmAsync(_orchestrator.ContinueCodexAsync, TelegramText.ContinuingCodex(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.RecoverClaude => await ExecuteAndConfirmAsync(_orchestrator.RecoverClaudeAsync, TelegramText.RecoveringClaude(language), cancellationToken).ConfigureAwait(false),
                TelegramCommand.RecoverCodex => await ExecuteAndConfirmAsync(_orchestrator.RecoverCodexAsync, TelegramText.RecoveringCodex(language), cancellationToken).ConfigureAwait(false),
                _ => TelegramText.HelpText(language),
            };
        }
        catch (Exception ex)
        {
            // InvalidOperationException is how every one of these commands
            // reports "not right now" — still working, wrong state, nothing to
            // resume. That is an answer to relay, not a fault to hide behind a
            // generic failure message.
            _logger.LogWarning(ex, "Telegram command {Command} failed.", command);
            reply = TelegramText.CouldNotDoThat(language, ex.Message);
        }

        await SendReplyAsync(token, chatId, TelegramMessageFormatting.Prefixed(configuration, reply), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs one no-argument orchestrator command and reports the fixed text that means it worked.</summary>
    private static async Task<string> ExecuteAndConfirmAsync(
        Func<CancellationToken, Task> action, string confirmation, CancellationToken cancellationToken)
    {
        await action(cancellationToken).ConfigureAwait(false);
        return confirmation;
    }

    private async Task<string> StatusTextAsync(TelegramLanguage language, CancellationToken cancellationToken)
    {
        var status = await _orchestrator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var mode = TelegramText.ModeLabel(language, status.DryRun);
        var stateText = TelegramText.StateLabel(language, status.StatusText);
        var lines = new List<string>
        {
            $"{stateText} ({mode})",
            TelegramText.IterationLabel(language, status.CurrentIteration, status.MaximumIterations),
            $"Claude: {status.ClaudeStatus}  ·  Codex: {status.CodexStatus}",
        };

        if (!string.IsNullOrWhiteSpace(status.GitBranch))
        {
            lines.Add(TelegramText.BranchLabel(language, status.GitBranch));
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            lines.Add(TelegramText.LastErrorLabel(language, status.LastError));
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Reads one or more chat ids out of a settings field — one per line, or
    /// comma-separated on one line, matching how <c>AgentEnvironment</c> reads
    /// its own multi-value field. Blank entries are dropped.
    /// </summary>
    private static HashSet<string> ParseChatIds(string? raw)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ids;
        }

        foreach (var candidate in raw.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length > 0)
            {
                ids.Add(trimmed);
            }
        }

        return ids;
    }

    private async Task SendReplyAsync(string token, string chatId, string text, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = text.Length <= 4096 ? text : text[..4095] + "…",
            ["disable_web_page_preview"] = "true",
        };

        try
        {
            using var response = await _httpClient
                .PostAsync(
                    $"https://api.telegram.org/bot{token}/sendMessage",
                    new FormUrlEncodedContent(payload),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Telegram reply failed with status {Status}.", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Telegram reply failed to send.");
        }
    }

    private sealed record TelegramUpdatesResponse
    {
        [JsonPropertyName("result")]
        public List<TelegramUpdate>? Result { get; init; }
    }

    private sealed record TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; init; }

        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; init; }
    }

    private sealed record TelegramMessage
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("chat")]
        public TelegramChat? Chat { get; init; }
    }

    private sealed record TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }
    }
}
