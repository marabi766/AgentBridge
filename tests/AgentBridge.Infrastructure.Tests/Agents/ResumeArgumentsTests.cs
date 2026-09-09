using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

/// <summary>
/// How each CLI is asked to bring its last session back.
///
/// This matters more than it looks. A command line run is a new process with no
/// memory of the last one, so "continue" sent without these arguments arrives at
/// an agent that has no idea what it is being asked to continue. It does not
/// fail — it quietly does something else, which is worse.
/// </summary>
public sealed class ResumeArgumentsTests
{
    private static readonly ClaudeCliAdapter Claude =
        new(NoConfiguration.Instance, NullLogger<ClaudeCliAdapter>.Instance);

    private static readonly CodexCliAdapter Codex =
        new(NoConfiguration.Instance, NullLogger<CodexCliAdapter>.Instance);

    [Fact]
    public void ClaudeIsAskedToContinueTheConversationInThisDirectory() =>
        Assert.Equal(
            "--continue --print --verbose --dangerously-skip-permissions",
            Claude.ResumeArguments("--print --verbose --dangerously-skip-permissions"));

    [Fact]
    public void ClaudeKeepsEverythingElseTheOperatorConfigured()
    {
        // A model or output format chosen for normal runs has to apply to a
        // resumed one too, or the same agent answers in a different shape
        // depending on which button was pressed.
        var resumed = Claude.ResumeArguments("--print --output-format stream-json --model claude-opus-5");

        Assert.Contains("--output-format stream-json", resumed);
        Assert.Contains("--model claude-opus-5", resumed);
    }

    [Theory]
    [InlineData("--continue --print")]
    [InlineData("-c --print")]
    public void ClaudeIsNotToldToContinueTwice(string alreadyResuming) =>
        Assert.Equal(alreadyResuming, Claude.ResumeArguments(alreadyResuming));

    [Fact]
    public void CodexResumesThroughItsSubcommand() =>
        // "resume" belongs after the verb. In front it would become the top-level
        // command and take exec — and the non-interactive behaviour that depends
        // on it — with it.
        Assert.Equal(
            "exec resume --last --sandbox workspace-write --skip-git-repo-check -",
            Codex.ResumeArguments("exec --sandbox workspace-write --skip-git-repo-check -"));

    [Fact]
    public void CodexKeepsReadingThePromptFromStandardInput() =>
        // The trailing "-" is what makes a long multi-line nudge safe to pass.
        // Losing it in the rewrite would send the prompt nowhere.
        Assert.EndsWith(" -", Codex.ResumeArguments(new BridgeConfiguration().CodexCliArguments));

    [Fact]
    public void CodexIsNotToldToResumeTwice()
    {
        const string already = "exec resume --last --sandbox workspace-write -";

        Assert.Equal(already, Codex.ResumeArguments(already));
    }

    [Fact]
    public void ArgumentsWithNoVerbToAttachToAreLeftAlone() =>
        // Guessing a command line here would produce a run that looks like it
        // resumed and did not. Leaving it visibly unchanged is the honest answer.
        Assert.Equal("--help", Codex.ResumeArguments("--help"));

    /// <summary>
    /// Rewriting arguments never reads configuration, so these tests need none.
    /// </summary>
    private sealed class NoConfiguration : IConfigurationService
    {
        public static readonly NoConfiguration Instance = new();

        public Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BridgeConfiguration());

        public Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
