using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Infrastructure.Services;

public sealed class ProjectService(IGitService gitService) : IProjectService
{
    public Task<ProjectValidationResult> ValidateProjectPathAsync(string projectPath, CancellationToken cancellationToken) =>
        ValidateProjectAsync(
            BridgeConfiguration.CreateDefault() with { ProjectPath = projectPath },
            cancellationToken);

    public async Task<ProjectValidationResult> ValidateProjectAsync(
        BridgeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var projectPath = configuration.ProjectPath;
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

        var claudeNameError = GetProtocolFileNameValidationError(configuration.ClaudeReportFileName, "Claude report");
        var codexNameError = GetProtocolFileNameValidationError(configuration.CodexPromptFileName, "Codex prompt");
        if (claudeNameError is not null) errors.Add(claudeNameError);
        if (codexNameError is not null) errors.Add(codexNameError);

        var claudeReportExists = pathExists && claudeNameError is null &&
            File.Exists(ResolveProtocolFilePath(projectPath, configuration.ClaudeReportFileName));
        var codexPromptExists = pathExists && codexNameError is null &&
            File.Exists(ResolveProtocolFilePath(projectPath, configuration.CodexPromptFileName));

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
            IsValid = pathExists && errors.Count == 0,
            PathExists = pathExists,
            IsGitRepository = isGitRepository,
            ClaudeReportFileExists = claudeReportExists,
            CodexPromptFileExists = codexPromptExists,
            Errors = errors,
            Warnings = warnings,
        };
    }

    public string GetClaudeReportFilePath(BridgeConfiguration configuration) =>
        ResolveProtocolFilePath(configuration.ProjectPath, configuration.ClaudeReportFileName);

    public string GetCodexPromptFilePath(BridgeConfiguration configuration) =>
        ResolveProtocolFilePath(configuration.ProjectPath, configuration.CodexPromptFileName);

    internal static string? GetProtocolFileNameValidationError(string? fileName, string label)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"{label} file name cannot be empty.";
        }

        if (Path.IsPathRooted(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName is "." or ".." ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return $"{label} file name must be a simple file name in the project root, not a path: {fileName}";
        }

        return null;
    }

    private static string ResolveProtocolFilePath(string projectPath, string fileName)
    {
        var error = GetProtocolFileNameValidationError(fileName, "Protocol");
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(fileName));
        }

        var root = Path.GetFullPath(projectPath);
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Protocol file must remain inside the configured project root.", nameof(fileName));
        }

        return candidate;
    }
}
