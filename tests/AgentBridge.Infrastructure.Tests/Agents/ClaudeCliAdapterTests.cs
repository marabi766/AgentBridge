using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Agents;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

public sealed class ClaudeCliAdapterTests
{
    [Fact]
    public void DefaultConfiguration_RunsClaudeNonInteractivelyAndReadsThePromptFromStandardInput()
    {
        // The instruction is long and multi-line; passing it as a command line
        // argument would put its quoting at the mercy of the shell. With --print
        // and no prompt argument the CLI reads it from stdin instead.
        var configuration = new BridgeConfiguration();

        Assert.True(configuration.UseClaudeCli);
        Assert.Equal("claude", configuration.ClaudeCliExecutable);
        Assert.Equal(
            new[] { "--print", "--permission-mode", "acceptEdits" },
            CommandLineAgentAdapter.SplitArguments(configuration.ClaudeCliArguments));
    }

    [Fact]
    public void AClaudeRunIsAllowedToTakeLongerThanACodexOne()
    {
        // Claude does the implementation and Codex only reviews it and writes the
        // next prompt, so equal deadlines would cut the longer job short first.
        var configuration = new BridgeConfiguration();

        Assert.True(configuration.ClaudeCliTimeoutSeconds >= configuration.CodexCliTimeoutSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveExecutable_TreatsNothingConfiguredAsNotFound(string? configured) =>
        Assert.Null(CommandLineAgentAdapter.ResolveExecutable(configured));

    [Fact]
    public void ResolveExecutable_AcceptsAFullPathOnlyWhenTheFileIsActuallyThere()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"agent-bridge-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(existing, "@echo off");
        try
        {
            Assert.Equal(existing, CommandLineAgentAdapter.ResolveExecutable(existing));
            Assert.Null(CommandLineAgentAdapter.ResolveExecutable(existing + ".missing"));
        }
        finally
        {
            File.Delete(existing);
        }
    }
}
