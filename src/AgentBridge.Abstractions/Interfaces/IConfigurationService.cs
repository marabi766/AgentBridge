using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

public interface IConfigurationService
{
    Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken);
}
