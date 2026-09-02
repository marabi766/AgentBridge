using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Infrastructure.Services;

public sealed class DefaultAgentAdapterProvider : IAgentAdapterProvider
{
    private readonly Dictionary<AgentRole, IAgentAdapter> _adapters;

    public DefaultAgentAdapterProvider(IEnumerable<IAgentAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(a => a.Role);
    }

    public IAgentAdapter GetAdapter(AgentRole role) =>
        _adapters.TryGetValue(role, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"No IAgentAdapter registered for role '{role}'.");
}
