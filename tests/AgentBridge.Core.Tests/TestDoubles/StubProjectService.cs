using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

public sealed class StubProjectService : IProjectService
{
    public bool IsValid { get; set; } = true;

    public string[] Errors { get; set; } = [];

    public Task<ProjectValidationResult> ValidateProjectPathAsync(string projectPath, CancellationToken cancellationToken) =>
        Task.FromResult(new ProjectValidationResult
        {
            IsValid = IsValid,
            PathExists = IsValid,
            IsGitRepository = true,
            ClaudeReportFileExists = false,
            CodexPromptFileExists = false,
            Errors = Errors,
        });

    public string GetClaudeReportFilePath(BridgeConfiguration configuration) =>
        Path.Combine(configuration.ProjectPath, configuration.ClaudeReportFileName);

    public string GetCodexPromptFilePath(BridgeConfiguration configuration) =>
        Path.Combine(configuration.ProjectPath, configuration.CodexPromptFileName);
}
