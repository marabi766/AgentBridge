using AgentBridge.Core.Templates;

namespace AgentBridge.Core.Tests.Templates;

public class PlaceholderTemplateEngineTests
{
    private readonly PlaceholderTemplateEngine _engine = new();

    [Fact]
    public void Render_SubstitutesKnownPlaceholders()
    {
        var result = _engine.Render("Hello {{name}}, iteration {{iteration}}.", new Dictionary<string, string>
        {
            ["name"] = "Codex",
            ["iteration"] = "3",
        });

        Assert.Equal("Hello Codex, iteration 3.", result);
    }

    [Fact]
    public void Render_LeavesUnknownPlaceholdersVerbatim()
    {
        var result = _engine.Render("Value: {{unknown}}", new Dictionary<string, string>());
        Assert.Equal("Value: {{unknown}}", result);
    }

    [Fact]
    public void Render_TolerantOfWhitespaceInsideBraces()
    {
        var result = _engine.Render("{{ name }}", new Dictionary<string, string> { ["name"] = "x" });
        Assert.Equal("x", result);
    }

    [Fact]
    public void Render_SamePlaceholderMultipleTimes_AllReplaced()
    {
        var result = _engine.Render("{{x}}-{{x}}-{{x}}", new Dictionary<string, string> { ["x"] = "a" });
        Assert.Equal("a-a-a", result);
    }

    [Fact]
    public void TemplateVariableBuilder_ProducesExpectedKeys()
    {
        var vars = TemplateVariableBuilder.Build("C:/proj", 2, 50, "ClaudeResultReport.md", "CodexPrompt.md", "main", "hashA", "hashB");

        Assert.Equal("C:/proj", vars[TemplateVariableNames.ProjectPath]);
        Assert.Equal("2", vars[TemplateVariableNames.Iteration]);
        Assert.Equal("50", vars[TemplateVariableNames.MaxIterations]);
        Assert.Equal("ClaudeResultReport.md", vars[TemplateVariableNames.ReportFile]);
        Assert.Equal("CodexPrompt.md", vars[TemplateVariableNames.PromptFile]);
        Assert.Equal("main", vars[TemplateVariableNames.CurrentBranch]);
        Assert.Equal("hashA", vars[TemplateVariableNames.LastClaudeReportHash]);
        Assert.Equal("hashB", vars[TemplateVariableNames.LastCodexPromptHash]);
    }

    [Fact]
    public void TemplateVariableBuilder_NullBranchAndHashes_FallBackToPlaceholderText()
    {
        var vars = TemplateVariableBuilder.Build("C:/proj", 1, 10, "a.md", "b.md", null, null, null);

        Assert.Equal("unknown", vars[TemplateVariableNames.CurrentBranch]);
        Assert.Equal("none", vars[TemplateVariableNames.LastClaudeReportHash]);
        Assert.Equal("none", vars[TemplateVariableNames.LastCodexPromptHash]);
    }
}
