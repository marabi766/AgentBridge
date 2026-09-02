using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Infrastructure.Services;

public sealed class AgentDiagnosticsService(IAgentAdapterProvider adapterProvider) : IAgentDiagnosticsService
{
    public Task<string> GetClaudeDiagnosticsAsync(CancellationToken cancellationToken) =>
        adapterProvider.GetAdapter(AgentRole.Claude).GetDiagnosticsAsync(cancellationToken);

    public Task<string> GetCodexDiagnosticsAsync(CancellationToken cancellationToken) =>
        adapterProvider.GetAdapter(AgentRole.Codex).GetDiagnosticsAsync(cancellationToken);
}
