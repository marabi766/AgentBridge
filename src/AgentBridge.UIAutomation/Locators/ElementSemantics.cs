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

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
