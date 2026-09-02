using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

public interface IProjectService
{
    Task<ProjectValidationResult> ValidateProjectPathAsync(string projectPath, CancellationToken cancellationToken);

    Task<ProjectValidationResult> ValidateProjectAsync(BridgeConfiguration configuration, CancellationToken cancellationToken);

    string GetClaudeReportFilePath(BridgeConfiguration configuration);

    string GetCodexPromptFilePath(BridgeConfiguration configuration);
}
