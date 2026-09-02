namespace AgentBridge.Abstractions.Interfaces;

public interface IAgentDiagnosticsService
{
    Task<string> GetClaudeDiagnosticsAsync(CancellationToken cancellationToken);

    Task<string> GetCodexDiagnosticsAsync(CancellationToken cancellationToken);
}
