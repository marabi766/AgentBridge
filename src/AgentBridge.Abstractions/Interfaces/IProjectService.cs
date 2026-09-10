using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

public interface IProjectService
{
    Task<ProjectValidationResult> ValidateProjectPathAsync(string projectPath, CancellationToken cancellationToken);

    Task<ProjectValidationResult> ValidateProjectAsync(BridgeConfiguration configuration, CancellationToken cancellationToken);

    string GetClaudeReportFilePath(BridgeConfiguration configuration);

    string GetCodexPromptFilePath(BridgeConfiguration configuration);

    /// <summary>
    /// When a file was last written, in UTC, or null if it does not exist or
    /// cannot be read. The orchestrator uses this to tell a genuine reply from a
    /// leftover — the protocol alternates, so a report older than the current
    /// prompt is from an earlier cycle.
    /// </summary>
    DateTimeOffset? GetLastWriteTimeUtc(string path);
}
