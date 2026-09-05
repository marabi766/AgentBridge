using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Agents;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

public sealed class CodexCliAdapterTests
{
    [Fact]
    public void SplitArguments_SplitsTheDefaultInvocationIntoSeparateArguments()
    {
        var arguments = CodexCliAdapter.SplitArguments(new BridgeConfiguration().CodexCliArguments);

        Assert.Equal(
            new[] { "exec", "--sandbox", "workspace-write", "--skip-git-repo-check", "-" },
            arguments);
    }

    [Fact]
    public void SplitArguments_KeepsAQuotedValueTogether()
    {
        // Arguments are passed as a list rather than one command line, so a value
        // containing spaces has to survive as a single element or the process
        // receives it as several.
        var arguments = CodexCliAdapter.SplitArguments("exec --cd \"C:\\Program Files\\work\" -");

        Assert.Equal(new[] { "exec", "--cd", @"C:\Program Files\work", "-" }, arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SplitArguments_TreatsNothingConfiguredAsNoArguments(string? arguments) =>
        Assert.Empty(CodexCliAdapter.SplitArguments(arguments));

    [Fact]
    public void SplitArguments_IgnoresRepeatedAndTrailingWhitespace() =>
        Assert.Equal(
            new[] { "exec", "-" },
            CodexCliAdapter.SplitArguments("  exec    -   "));

    [Fact]
    public void DefaultConfiguration_ReadsThePromptFromStandardInput()
    {
        // The instruction is long and multi-line; passing it as a command line
        // argument would put its quoting at the mercy of the shell. The trailing
        // "-" is what tells Codex to read it from stdin instead.
        var configuration = new BridgeConfiguration();

        Assert.False(configuration.UseCodexCli);
        Assert.Equal("codex", configuration.CodexCliExecutable);
        Assert.EndsWith(" -", configuration.CodexCliArguments);
        Assert.Contains("workspace-write", configuration.CodexCliArguments);
    }
}
