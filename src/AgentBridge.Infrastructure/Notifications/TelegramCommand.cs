namespace AgentBridge.Infrastructure.Notifications;

public enum TelegramCommand
{
    Run,
    Stop,
    Pause,
    Resume,
    Status,
    RetryClaude,
    RetryCodex,
    ContinueClaude,
    ContinueCodex,
    RecoverClaude,
    RecoverCodex,
    Help,
}

/// <summary>
/// Reads a Telegram slash command out of a message's text.
///
/// Kept separate from the listener that acts on it for the same reason
/// <see cref="AgentQuotaSignal"/> and <see cref="RemoteControlSignal"/> are
/// separate from what reads their output: parsing is a pure function of a
/// string and is worth testing as one, without a fake orchestrator or an HTTP
/// call anywhere nearby.
/// </summary>
public static class TelegramCommandParser
{
    private static readonly Dictionary<string, TelegramCommand> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        // Deliberately NOT "start": that word is not ours to claim. Telegram's
        // own client sends a literal "/start" the first time anyone opens a chat
        // with a bot, before they have read anything it says — it is onboarding
        // chrome, not a considered instruction. Mapping it to actually starting
        // the bridge would run a build/test loop the moment someone so much as
        // opened the conversation. "/start" is handled below as Help instead;
        // starting the bridge itself is "/run".
        ["run"] = TelegramCommand.Run,
        ["start"] = TelegramCommand.Help,
        ["stop"] = TelegramCommand.Stop,
        ["pause"] = TelegramCommand.Pause,
        ["resume"] = TelegramCommand.Resume,
        ["status"] = TelegramCommand.Status,
        ["retry_claude"] = TelegramCommand.RetryClaude,
        ["retryclaude"] = TelegramCommand.RetryClaude,
        ["retry_codex"] = TelegramCommand.RetryCodex,
        ["retrycodex"] = TelegramCommand.RetryCodex,
        ["continue_claude"] = TelegramCommand.ContinueClaude,
        ["continueclaude"] = TelegramCommand.ContinueClaude,
        ["continue_codex"] = TelegramCommand.ContinueCodex,
        ["continuecodex"] = TelegramCommand.ContinueCodex,
        ["recover_claude"] = TelegramCommand.RecoverClaude,
        ["recoverclaude"] = TelegramCommand.RecoverClaude,
        ["recover_codex"] = TelegramCommand.RecoverCodex,
        ["recovercodex"] = TelegramCommand.RecoverCodex,
        ["help"] = TelegramCommand.Help,
    };

    /// <summary>
    /// Reads the command out of a message, or null when the text is not one —
    /// ordinary chat, a caption, anything not meant for the bridge at all.
    /// </summary>
    public static TelegramCommand? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
        {
            return null;
        }

        // A command can carry "@BotName" (Telegram appends this in group chats)
        // and arguments after it; only the word itself is read.
        var word = text[1..].Split([' ', '@'], 2)[0];
        return Commands.TryGetValue(word, out var command) ? command : null;
    }
}
