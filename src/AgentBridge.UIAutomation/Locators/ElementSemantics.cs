using System.Text.RegularExpressions;

namespace AgentBridge.UIAutomation.Locators;

public static partial class ElementSemantics
{
    public static string Normalize(string? value) =>
        Whitespace().Replace(value?.Trim() ?? string.Empty, " ");

    public static bool IsCurrentConversationMarker(string? controlType, string? name, string? className, string identifier)
    {
        if (!string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedName = Normalize(name);
        var normalizedIdentifier = Normalize(identifier);
        if (normalizedIdentifier.Length == 0)
        {
            return false;
        }

        var exactTitle = string.Equals(normalizedName, normalizedIdentifier, StringComparison.OrdinalIgnoreCase);
        var renameTitle = string.Equals(
            normalizedName,
            $"{normalizedIdentifier}, rename session",
            StringComparison.OrdinalIgnoreCase);
        if (!exactTitle && !renameTitle)
        {
            return false;
        }

        var css = className ?? string.Empty;
        return !css.Contains("sidebar-item", StringComparison.OrdinalIgnoreCase)
            && !css.Contains("df-row", StringComparison.OrdinalIgnoreCase)
            && !css.Contains("folder-row", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPreferredCurrentConversationMarker(string? controlType, string? name, string identifier) =>
        string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            Normalize(name),
            $"{Normalize(identifier)}, rename session",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsConversationNavigationCandidate(
        string? controlType,
        string? name,
        string? className,
        string identifier) =>
        string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Normalize(name), Normalize(identifier), StringComparison.OrdinalIgnoreCase)
        && (className?.Contains("sidebar-item", StringComparison.OrdinalIgnoreCase) ?? false)
        && !(className?.Contains("folder-row", StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool IsInputCandidate(string? controlType, string? name, string? className)
    {
        if (!string.Equals(controlType, "Edit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedName = Normalize(name);
        var css = className ?? string.Empty;
        return normalizedName is "Prompt" or "Do anything" or "Message Claude" or "Message ChatGPT" or "Ask anything"
            || css.Contains("ProseMirror", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSendButton(string? controlType, string? name) =>
        string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Normalize(name), "Send", StringComparison.OrdinalIgnoreCase);

    public static bool IsProcessingButton(string? controlType, string? name) =>
        string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Normalize(name), "Stop", StringComparison.OrdinalIgnoreCase);

    public static bool IsQuotaLimitMarker(string? name) =>
        string.Equals(Normalize(name), "Session limit reached", StringComparison.OrdinalIgnoreCase);

    public static bool IsQuotaResetMarker(string? name) =>
        Normalize(name).StartsWith("Resets in ", StringComparison.OrdinalIgnoreCase);

    public static bool IsExactRenderedReceipt(string? renderedName, string message)
    {
        var rendered = Normalize(renderedName);
        var expected = Normalize(message);
        return string.Equals(rendered, expected, StringComparison.Ordinal)
            || string.Equals(rendered, $"You said: {expected}", StringComparison.Ordinal);
    }

    public static bool IsEditorPlaceholder(string? value)
    {
        var normalized = Normalize(value);
        return normalized is "Type / for commands" or "Do anything" or "Prompt" or "Ask anything";
    }

    // Chromium initially exposes only a tiny shell tree (RootView, NonClientView,
    // caption buttons) to UI Automation and publishes its renderer document once
    // the accessibility tree has been enabled. The presence of that document root
    // is the actual warm-up signal; the number of shell elements is not, because a
    // fully warmed window can legitimately expose few controls while a shell-only
    // window can expose many.
    public static bool IsRendererDocumentRoot(string? controlType, string? automationId) =>
        string.Equals(controlType, "Document", StringComparison.OrdinalIgnoreCase)
        && Normalize(automationId).Length > 0;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
