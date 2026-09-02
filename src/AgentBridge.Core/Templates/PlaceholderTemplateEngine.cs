using System.Text;
using System.Text.RegularExpressions;
using AgentBridge.Abstractions.Interfaces;

namespace AgentBridge.Core.Templates;

/// <summary>
/// Renders <c>{{variableName}}</c> placeholders. Unknown placeholders are left
/// verbatim so a user-edited template with a typo doesn't blow up the whole message.
/// </summary>
public sealed partial class PlaceholderTemplateEngine : ITemplateEngine
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    public string Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        return PlaceholderPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return variables.TryGetValue(key, out var value) ? value : match.Value;
        });
    }
}

public static class TemplateVariableNames
{
    public const string ProjectPath = "projectPath";
    public const string Iteration = "iteration";
    public const string MaxIterations = "maxIterations";
    public const string ReportFile = "reportFile";
    public const string PromptFile = "promptFile";
    public const string CurrentBranch = "currentBranch";
    public const string LastClaudeReportHash = "lastClaudeReportHash";
    public const string LastCodexPromptHash = "lastCodexPromptHash";
}

public static class TemplateVariableBuilder
{
    public static Dictionary<string, string> Build(
        string projectPath,
        int iteration,
        int maxIterations,
        string reportFileName,
        string promptFileName,
        string? currentBranch,
        string? lastClaudeReportHash,
        string? lastCodexPromptHash)
    {
        return new Dictionary<string, string>
        {
            [TemplateVariableNames.ProjectPath] = projectPath,
            [TemplateVariableNames.Iteration] = iteration.ToString(),
            [TemplateVariableNames.MaxIterations] = maxIterations.ToString(),
            [TemplateVariableNames.ReportFile] = reportFileName,
            [TemplateVariableNames.PromptFile] = promptFileName,
            [TemplateVariableNames.CurrentBranch] = currentBranch ?? "unknown",
            [TemplateVariableNames.LastClaudeReportHash] = lastClaudeReportHash ?? "none",
            [TemplateVariableNames.LastCodexPromptHash] = lastCodexPromptHash ?? "none",
        };
    }
}
