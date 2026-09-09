using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Persistence;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Infrastructure.Tests.Services;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abtests-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsServiceTests() => Directory.CreateDirectory(_dir);

    private SettingsService CreateService() =>
        new(new JsonConfigurationService(Path.Combine(_dir, "settings.json"), NullLogger<JsonConfigurationService>.Instance));

    [Fact]
    public async Task Validate_NonExistentProjectPath_Fails()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with { ProjectPath = Path.Combine(_dir, "does-not-exist") };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not exist"));
    }

    [Fact]
    public async Task Validate_SameFileNameForBothReports_Fails()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = _dir,
            ClaudeReportFileName = "same.md",
            CodexPromptFileName = "same.md",
        };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("..\\outside.md")]
    [InlineData("nested/report.md")]
    [InlineData("C:\\outside.md")]
    public async Task Validate_ProtocolFileNameWithPath_IsRejected(string unsafeName)
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = _dir,
            ClaudeReportFileName = unsafeName,
        };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("simple file name"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Validate_NonPositiveMaximumIterations_Fails(int maxIterations)
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with { ProjectPath = _dir, MaximumIterations = maxIterations };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validate_LiveModeWithoutBothConversationIdentifiers_Fails()
    {
        // Both transports are named explicitly. The rule is about the desktop
        // route, and the default is now the command line — leaving it implicit
        // would make this test quietly stop exercising anything.
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = _dir,
            DryRun = false,
            UseClaudeCli = false,
            UseCodexCli = false,
            ClaudeConversationIdentifier = "Claude target",
        };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Codex conversation identifier"));
    }

    [Fact]
    public async Task Validate_LiveModeWithBothConversationIdentifiers_Succeeds()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = _dir,
            DryRun = false,
            ClaudeConversationIdentifier = "Claude target",
            CodexConversationIdentifier = "Codex target",
        };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task UpdateAsync_ValidConfiguration_PersistsIt()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with { ProjectPath = _dir, MaximumIterations = 7 };

        var result = await service.UpdateAsync(config, CancellationToken.None);
        Assert.True(result.IsValid);

        var reloaded = await service.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(7, reloaded.MaximumIterations);
    }

    [Fact]
    public async Task UpdateAsync_InvalidConfiguration_NeverPersists()
    {
        var service = CreateService();
        var good = BridgeConfiguration.CreateDefault() with { ProjectPath = _dir, MaximumIterations = 7 };
        await service.UpdateAsync(good, CancellationToken.None);

        var bad = good with { MaximumIterations = -5 };
        var result = await service.UpdateAsync(bad, CancellationToken.None);
        Assert.False(result.IsValid);

        var stillGood = await service.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(7, stillGood.MaximumIterations); // the bad config never overwrote the good one
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// A configuration that would pass on every count except the one each test
    /// below deliberately breaks, so a failure names the rule under test rather
    /// than something incidental.
    /// </summary>
    private BridgeConfiguration ValidLiveConfiguration() => BridgeConfiguration.CreateDefault() with
    {
        ProjectPath = _dir,
        DryRun = false,
        ClaudeConversationIdentifier = "a chat",
        CodexConversationIdentifier = "another chat",
    };

    [Fact]
    public async Task ALiveRunOnTheCommandLineDoesNotNeedAConversationTitle()
    {
        // A command line has no window, no chat list and nothing a title could
        // select, so requiring one would force the operator to invent a value
        // that is never read — and, until this, blocked the save outright.
        var result = await CreateService().ValidateAsync(ValidLiveConfiguration() with
        {
            UseClaudeCli = true,
            UseCodexCli = true,
            ClaudeConversationIdentifier = null,
            CodexConversationIdentifier = null,
        }, CancellationToken.None);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task ALiveRunThroughTheDesktopWindowStillNeedsItsTitle()
    {
        var result = await CreateService().ValidateAsync(ValidLiveConfiguration() with
        {
            UseClaudeCli = false,
            UseCodexCli = false,
            ClaudeConversationIdentifier = null,
            CodexConversationIdentifier = null,
        }, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task EachAgentIsJudgedByItsOwnTransport()
    {
        // Claude on the command line, Codex in its window: only the second needs
        // a title, and a rule that looked at one flag for both would get this
        // mixed configuration wrong in one direction or the other.
        var result = await CreateService().ValidateAsync(ValidLiveConfiguration() with
        {
            UseClaudeCli = true,
            UseCodexCli = false,
            ClaudeConversationIdentifier = null,
            CodexConversationIdentifier = null,
        }, CancellationToken.None);

        Assert.False(result.IsValid);
        var only = Assert.Single(result.Errors);
        Assert.Contains("Codex", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALiveCommandLineRunNeedsSomethingToRun()
    {
        var result = await CreateService().ValidateAsync(ValidLiveConfiguration() with
        {
            UseClaudeCli = true,
            ClaudeCliExecutable = "   ",
        }, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Claude CLI executable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task ARunTimeoutThatIsNotPositiveIsRejected(int seconds)
    {
        // Zero would cancel every run the instant it started, which looks exactly
        // like an agent that refuses to do anything.
        var result = await CreateService().ValidateAsync(
            ValidLiveConfiguration() with { ClaudeCliTimeoutSeconds = seconds }, CancellationToken.None);

        Assert.False(result.IsValid);
    }
}
