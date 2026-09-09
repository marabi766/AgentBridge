using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Agents;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Services;

public sealed class DefaultAgentAdapterProviderTests
{
    [Fact]
    public void EachAgentsTransportIsChosenIndependently()
    {
        // The two settings are separate on purpose: driving Claude through its
        // command line while Codex still runs in the ChatGPT window is a real
        // configuration, and one shared flag would make it unreachable.
        var configuration = new MutableConfigurationService(new BridgeConfiguration
        {
            UseClaudeCli = true,
            UseCodexCli = false,
        });
        var provider = BuildProvider(configuration);

        Assert.IsType<ClaudeCliAdapter>(provider.GetAdapter(AgentRole.Claude));
        Assert.IsType<FakeDesktopAdapter>(provider.GetAdapter(AgentRole.Codex));
    }

    [Fact]
    public void ChangingTheSettingChangesWhichAdapterRunsWithoutARestart()
    {
        var configuration = new MutableConfigurationService(new BridgeConfiguration { UseCodexCli = false });
        var provider = BuildProvider(configuration);

        Assert.IsType<FakeDesktopAdapter>(provider.GetAdapter(AgentRole.Codex));

        configuration.Configuration = configuration.Configuration with { UseCodexCli = true };

        Assert.IsType<CodexCliAdapter>(provider.GetAdapter(AgentRole.Codex));
    }

    [Fact]
    public void AnUnreadableSettingsFileKeepsTheLastKnownChoiceInsteadOfSwitchingAgents()
    {
        var configuration = new MutableConfigurationService(new BridgeConfiguration { UseCodexCli = true });
        var provider = BuildProvider(configuration);
        Assert.IsType<CodexCliAdapter>(provider.GetAdapter(AgentRole.Codex));

        configuration.FailNextLoads = true;

        Assert.IsType<CodexCliAdapter>(provider.GetAdapter(AgentRole.Codex));
    }

    [Fact]
    public void ARoleWithOnlyOneAdapterIgnoresTheSettingEntirely()
    {
        // This is the headless host's shape: only command line adapters exist, so
        // there is nothing for the setting to select between.
        var configuration = new MutableConfigurationService(new BridgeConfiguration { UseCodexCli = false });
        var provider = new DefaultAgentAdapterProvider(
            [new CodexCliAdapter(configuration, NullLogger<CodexCliAdapter>.Instance)],
            configuration,
            NoCaching);

        Assert.IsType<CodexCliAdapter>(provider.GetAdapter(AgentRole.Codex));
    }

    [Fact]
    public void ARoleWithNoAdapterIsARegistrationMistakeAndSaysSo()
    {
        var configuration = new MutableConfigurationService(new BridgeConfiguration());
        var provider = new DefaultAgentAdapterProvider([], configuration, NoCaching);

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetAdapter(AgentRole.Claude));
        Assert.Contains("Claude", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The setting is re-read on every call, so a test can change it and assert
    /// the next call rather than sleeping out the production cache window.
    /// </summary>
    private static readonly TimeSpan NoCaching = TimeSpan.Zero;

    private static DefaultAgentAdapterProvider BuildProvider(MutableConfigurationService configuration) =>
        new(
            [
                new ClaudeCliAdapter(configuration, NullLogger<ClaudeCliAdapter>.Instance),
                new FakeDesktopAdapter(AgentRole.Claude),
                new CodexCliAdapter(configuration, NullLogger<CodexCliAdapter>.Instance),
                new FakeDesktopAdapter(AgentRole.Codex),
            ],
            configuration,
            NoCaching);

    /// <summary>
    /// Stands in for a settings file the operator is editing while the bridge
    /// runs, including the case where it cannot be read at that moment.
    /// </summary>
    private sealed class MutableConfigurationService(BridgeConfiguration configuration) : IConfigurationService
    {
        public BridgeConfiguration Configuration { get; set; } = configuration;

        public bool FailNextLoads { get; set; }

        public Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            FailNextLoads
                ? Task.FromException<BridgeConfiguration>(new IOException("settings.json is locked"))
                : Task.FromResult(Configuration);

        public Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken)
        {
            Configuration = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDesktopAdapter(AgentRole role) : IAgentAdapter
    {
        public string Name => $"{role} Desktop (fake)";

        public AgentRole Role => role;

        public bool SupportsRealMessageDelivery => false;

        public Task<bool> IsApplicationRunningAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> LaunchApplicationAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> IsProcessingAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ActivateAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> FindConversationAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> FindInputBoxAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(AgentStatus.Ready);

        public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken) => Task.FromResult(Name);
    }
}
