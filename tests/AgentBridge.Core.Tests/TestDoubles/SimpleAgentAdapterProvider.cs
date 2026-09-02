using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

public sealed class SimpleAgentAdapterProvider(IAgentAdapter claude, IAgentAdapter codex) : IAgentAdapterProvider
{
    public IAgentAdapter GetAdapter(AgentRole role) => role switch
    {
        AgentRole.Claude => claude,
        AgentRole.Codex => codex,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
