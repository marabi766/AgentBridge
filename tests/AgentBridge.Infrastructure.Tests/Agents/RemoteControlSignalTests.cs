using AgentBridge.Infrastructure.Agents;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

/// <summary>
/// The lines here were printed by the Claude CLI on 2026-09-10 while working out
/// how a remote control link could be obtained at all. They are quoted rather
/// than paraphrased for the reason the quota tests are: the first guess at what
/// a tool prints is usually wrong, and a paraphrase agrees with the guess.
/// </summary>
public sealed class RemoteControlSignalTests
{
    private const string RealBackgroundedOutput =
        """
        Starting background service…
        backgrounded · b627a19b (idle — send a prompt to start)
          claude agents             list sessions
          claude attach b627a19b    open in this terminal
          claude logs b627a19b      show recent output
          claude stop b627a19b      stop this session
        """;

    [Fact]
    public void TheShortIdIsReadFromWhatBackgroundingPrints() =>
        Assert.Equal("b627a19b", RemoteControlSignal.ReadSessionId(RealBackgroundedOutput));

    [Fact]
    public void TheIdIsTakenFromTheBackgroundedLineRatherThanTheHelpBelowIt()
    {
        // Every following line also contains the id. Reading the wrong one would
        // work by luck here and break the moment the help text changes.
        var reordered = "claude stop deadbeef      stop this session\nbackgrounded · b627a19b (idle)";

        Assert.Equal("b627a19b", RemoteControlSignal.ReadSessionId(reordered));
    }

    [Fact]
    public void ContinuingAConversationStillReportsItsNewId()
    {
        // --continue forks rather than resumes, and says so before the id.
        const string forked =
            "note: started a copy of that conversation as 754198c7. To continue a session under its own id, "
            + "pass its full session id to --resume.\nbackgrounded · 754198c7 (idle — send a prompt to start)";

        Assert.Equal("754198c7", RemoteControlSignal.ReadSessionId(forked));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Error: something went wrong")]
    public void NothingIsReadFromOutputThatAnnouncedNoSession(string? output) =>
        Assert.Null(RemoteControlSignal.ReadSessionId(output));

    [Fact]
    public void TheLinkIsReadOutOfTheTerminalTheSessionDrawsIn()
    {
        // The session's log is a full-screen render: escape sequences around the
        // text and a carriage return on every line. Reading it as plain text
        // finds a URL with colour codes welded to it, or none at all.
        const string painted =
            "[2K[1G[36m Remote Control [39m\r\n"
            + "[2K  Open [4mhttps://claude.ai/code/session_017ZtM7YFUZujzPtfHAGo75P[24m to drive\r\n";

        Assert.Equal(
            "https://claude.ai/code/session_017ZtM7YFUZujzPtfHAGo75P",
            RemoteControlSignal.ReadRemoteControlUrl(painted));
    }

    [Fact]
    public void APlainLinkIsReadJustAsWell() =>
        Assert.Equal(
            "https://claude.ai/code/session_01YTyns61ACCASpcXXwLjoZb",
            RemoteControlSignal.ReadRemoteControlUrl(
                "  Open https://claude.ai/code/session_01YTyns61ACCASpcXXwLjoZb on another device"));

    [Fact]
    public void ASessionThatHasNotPublishedItsLinkYetReportsNothing()
    {
        // What the log holds for the first few seconds. Returning anything here
        // would have the caller stop waiting and show a link that does not exist.
        const string stillConnecting = "[2K[1G\r\n[2K  Connecting…\r\n";

        Assert.Null(RemoteControlSignal.ReadRemoteControlUrl(stillConnecting));
    }

    [Theory]
    [InlineData("https://claude.ai/settings")]
    [InlineData("See https://docs.claude.com/code for details")]
    public void ALinkThatIsNotASessionIsNotMistakenForOne(string line) =>
        Assert.Null(RemoteControlSignal.ReadRemoteControlUrl(line));
}
