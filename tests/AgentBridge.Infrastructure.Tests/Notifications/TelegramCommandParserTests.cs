using AgentBridge.Infrastructure.Notifications;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Notifications;

public sealed class TelegramCommandParserTests
{
    [Theory]
    [InlineData("/run", TelegramCommand.Run)]
    [InlineData("/stop", TelegramCommand.Stop)]
    [InlineData("/pause", TelegramCommand.Pause)]
    [InlineData("/resume", TelegramCommand.Resume)]
    [InlineData("/status", TelegramCommand.Status)]
    [InlineData("/retry_claude", TelegramCommand.RetryClaude)]
    [InlineData("/retry_codex", TelegramCommand.RetryCodex)]
    [InlineData("/continue_claude", TelegramCommand.ContinueClaude)]
    [InlineData("/continue_codex", TelegramCommand.ContinueCodex)]
    [InlineData("/recover_claude", TelegramCommand.RecoverClaude)]
    [InlineData("/recover_codex", TelegramCommand.RecoverCodex)]
    [InlineData("/help", TelegramCommand.Help)]
    public void EachCommandWordIsRecognised(string text, TelegramCommand expected) =>
        Assert.Equal(expected, TelegramCommandParser.Parse(text));

    [Fact]
    public void CaseDoesNotMatter() =>
        Assert.Equal(TelegramCommand.Run, TelegramCommandParser.Parse("/RUN"));

    [Fact]
    public void TelegramsOwnAppendedBotNameIsIgnored() =>
        // Telegram appends "@YourBotName" in a group chat; the word after "/"
        // and before "@" is what identifies the command.
        Assert.Equal(TelegramCommand.Status, TelegramCommandParser.Parse("/status@AgentBridgeBot"));

    [Fact]
    public void ArgumentsAfterTheCommandAreIgnored() =>
        Assert.Equal(TelegramCommand.Run, TelegramCommandParser.Parse("/run please"));

    [Fact]
    public void BareSlashStartIsHelpNotRun()
    {
        // Telegram's client sends a literal "/start" the first time anyone opens
        // a chat with the bot — before they have read anything it says. Mapping
        // that to actually starting the bridge would run a build/test loop the
        // moment someone so much as opened the conversation.
        Assert.Equal(TelegramCommand.Help, TelegramCommandParser.Parse("/start"));
        Assert.NotEqual(TelegramCommand.Run, TelegramCommandParser.Parse("/start"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("run")]
    [InlineData("/unknown_command")]
    [InlineData("/")]
    public void AnythingThatIsNotARecognisedCommandReadsAsNothing(string? text) =>
        Assert.Null(TelegramCommandParser.Parse(text));
}
