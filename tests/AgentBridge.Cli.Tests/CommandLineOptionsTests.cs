using AgentBridge.Abstractions.Models;
using AgentBridge.Cli;
using Xunit;

namespace AgentBridge.Cli.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void AnInvocationWithNoOptionsChangesNothingThatWasStored()
    {
        // Running the host again with no arguments has to repeat the last run.
        // An option that defaulted to a value instead would quietly overwrite a
        // choice the operator made on purpose.
        var stored = new BridgeConfiguration
        {
            ProjectPath = @"C:\work\repo",
            MaximumIterations = 7,
            DryRun = false,
            ClaudeCliArguments = "--print --dangerously-skip-permissions",
            ClaudeCliTimeoutSeconds = 900,
        };

        var applied = CommandLineOptions.Parse([]).ApplyTo(stored);

        Assert.Equal(stored.ProjectPath, applied.ProjectPath);
        Assert.Equal(7, applied.MaximumIterations);
        Assert.False(applied.DryRun);
        Assert.Equal("--print --dangerously-skip-permissions", applied.ClaudeCliArguments);
        Assert.Equal(900, applied.ClaudeCliTimeoutSeconds);
    }

    [Fact]
    public void PassThroughArgumentsSurviveEvenThoughTheyLookLikeFlags()
    {
        // What --claude-args carries is another program's command line and starts
        // with a dash. Guarding it the way the other options are guarded would
        // make the option most likely to be set impossible to set.
        var options = CommandLineOptions.Parse(
            ["--claude-args", "--print --dangerously-skip-permissions", "--codex-args", "exec -"]);

        Assert.Null(options.ParseError);
        Assert.Equal("--print --dangerously-skip-permissions", options.ClaudeArguments);
        Assert.Equal("exec -", options.CodexArguments);
    }

    [Fact]
    public void AFlagWrittenWithoutItsValueDoesNotSwallowTheNextFlag()
    {
        var options = CommandLineOptions.Parse(["--project", "--live"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--project", options.ParseError, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeadlessHostAlwaysDrivesBothAgentsThroughTheirCommandLines()
    {
        // This process registers no desktop adapters at all, so a stored setting
        // saying otherwise would describe a configuration that cannot occur here.
        var applied = CommandLineOptions.Parse([]).ApplyTo(new BridgeConfiguration
        {
            ProjectPath = @"C:\work\repo",
            UseClaudeCli = false,
            UseCodexCli = false,
            AutoLaunchClaude = true,
            AutoLaunchChatGpt = true,
        });

        Assert.True(applied.UseClaudeCli);
        Assert.True(applied.UseCodexCli);
        Assert.False(applied.AutoLaunchClaude);
        Assert.False(applied.AutoLaunchChatGpt);
    }

    [Theory]
    [InlineData("claude", BridgeStartPoint.WaitForClaudeReport)]
    [InlineData("Codex", BridgeStartPoint.WaitForCodexPrompt)]
    public void StartPointNamesTheAgentWhoseFileIsWaitedFor(string value, BridgeStartPoint expected)
    {
        var options = CommandLineOptions.Parse(["--start-with", value]);

        Assert.Null(options.ParseError);
        Assert.Equal(expected, options.StartPoint);
    }

    [Fact]
    public void AnUnknownStartPointIsRejectedRatherThanQuietlyDefaulted()
    {
        // Starting at the wrong checkpoint sends the wrong agent the wrong file,
        // so a misspelling has to stop the run rather than pick one.
        var options = CommandLineOptions.Parse(["--start-with", "chatgpt"]);

        Assert.NotNull(options.ParseError);
        Assert.Null(options.StartPoint);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    public void AnIterationLimitThatIsNotAPositiveNumberIsRejected(string value)
    {
        var options = CommandLineOptions.Parse(["--max-iterations", value]);

        Assert.NotNull(options.ParseError);
    }

    [Fact]
    public void TheLastOfDryRunAndLiveWins()
    {
        Assert.False(CommandLineOptions.Parse(["--dry-run", "--live"]).DryRun);
        Assert.True(CommandLineOptions.Parse(["--live", "--dry-run"]).DryRun);
    }

    [Fact]
    public void AnUnrecognisedArgumentStopsTheRunInsteadOfBeingIgnored()
    {
        var options = CommandLineOptions.Parse(["--project", @"C:\work\repo", "--turbo"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--turbo", options.ParseError, StringComparison.Ordinal);
    }
}
