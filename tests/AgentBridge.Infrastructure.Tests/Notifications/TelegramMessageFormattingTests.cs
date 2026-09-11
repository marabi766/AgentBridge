using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Notifications;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Notifications;

public sealed class TelegramMessageFormattingTests
{
    [Theory]
    [InlineData(@"F:\Rasta", "Rasta")]
    [InlineData(@"F:\Rasta\", "Rasta")]
    [InlineData("/home/user/my-project", "my-project")]
    public void TheProjectLabelIsTheFolderNameNotTheFullPath(string projectPath, string expected)
    {
        var configuration = new BridgeConfiguration { ProjectPath = projectPath };

        Assert.Equal(expected, TelegramMessageFormatting.ProjectLabel(configuration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnconfiguredProjectFallsBackRatherThanShowingNothing(string? projectPath)
    {
        var configuration = new BridgeConfiguration { ProjectPath = projectPath ?? string.Empty };

        Assert.Equal("Agent Bridge", TelegramMessageFormatting.ProjectLabel(configuration));
    }

    [Fact]
    public void EveryMessageCarriesTheProjectNameAsAPrefix()
    {
        // The whole reason this exists: one operator can run the bridge against
        // more than one project through the same bot, and a message with no
        // project name is ambiguous the moment that happens.
        var configuration = new BridgeConfiguration { ProjectPath = @"F:\Rasta" };

        Assert.Equal("[Rasta] Codex finished.", TelegramMessageFormatting.Prefixed(configuration, "Codex finished."));
    }
}
