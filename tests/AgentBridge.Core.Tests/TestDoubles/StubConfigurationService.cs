using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

public sealed class StubConfigurationService(BridgeConfiguration configuration) : IConfigurationService
{
    public BridgeConfiguration Configuration { get; set; } = configuration;

    public Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Configuration);

    public Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken)
    {
        Configuration = configuration;
        return Task.CompletedTask;
    }
}
