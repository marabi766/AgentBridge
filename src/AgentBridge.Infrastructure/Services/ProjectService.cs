using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Infrastructure.Services;

public sealed class ProjectService(IGitService gitService) : IProjectService
{
    public async Task<ProjectValidationResult> ValidateProjectPathAsync(string projectPath, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            errors.Add("Project path is not set.");
            return new ProjectValidationResult
            {
                IsValid = false,
                PathExists = false,
                IsGitRepository = false,
                ClaudeReportFileExists = false,
                CodexPromptFileExists = false,
                Errors = errors,
            };
        }

        var pathExists = Directory.Exists(projectPath);
        if (!pathExists)
        {
            errors.Add($"Project path does not exist: {projectPath}");
        }

        var isGitRepository = false;
        if (pathExists)
        {
            var gitStatus = await gitService.GetStatusAsync(projectPath, cancellationToken).ConfigureAwait(false);
            isGitRepository = gitStatus.IsRepository;
            if (!isGitRepository)
            {
                warnings.Add("Project path is not a Git repository. The bridge will still function, but Git status will be unavailable.");
            }
        }

        var claudeReportPath = Path.Combine(projectPath, "ClaudeResultReport.md");
        var codexPromptPath = Path.Combine(projectPath, "CodexPrompt.md");
        var claudeReportExists = pathExists && File.Exists(claudeReportPath);
        var codexPromptExists = pathExists && File.Exists(codexPromptPath);

        if (pathExists && !claudeReportExists)
        {
            warnings.Add("ClaudeResultReport.md does not exist yet — it will be created once Claude writes its first report.");
        }

        if (pathExists && !codexPromptExists)
        {
            warnings.Add("CodexPrompt.md does not exist yet — it will be created once Codex writes its first prompt.");
        }

        return new ProjectValidationResult
        {
            IsValid = pathExists,
            PathExists = pathExists,
            IsGitRepository = isGitRepository,
            ClaudeReportFileExists = claudeReportExists,
            CodexPromptFileExists = codexPromptExists,
            Errors = errors,
            Warnings = warnings,
        };
    }

    public string GetClaudeReportFilePath(BridgeConfiguration configuration) =>
        Path.Combine(configuration.ProjectPath, configuration.ClaudeReportFileName);

    public string GetCodexPromptFilePath(BridgeConfiguration configuration) =>
        Path.Combine(configuration.ProjectPath, configuration.CodexPromptFileName);
}
