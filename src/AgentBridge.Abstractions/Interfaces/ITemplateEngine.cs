namespace AgentBridge.Abstractions.Interfaces;

public interface ITemplateEngine
{
    /// <summary>
    /// Renders a template containing <c>{{variableName}}</c> placeholders. Unknown
    /// placeholders are left verbatim in the output rather than throwing, so
    /// user-edited templates degrade gracefully.
    /// </summary>
    string Render(string template, IReadOnlyDictionary<string, string> variables);
}
