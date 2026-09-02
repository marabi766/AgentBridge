using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// Resolves the correct <see cref="IAgentAdapter"/> for a role. Exists so the
/// orchestrator can depend on a single abstraction instead of two same-typed
/// constructor parameters that DI cannot disambiguate on its own.
/// </summary>
public interface IAgentAdapterProvider
{
    IAgentAdapter GetAdapter(AgentRole role);
}
