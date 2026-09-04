using System.Text.RegularExpressions;

namespace AgentBridge.UIAutomation.Locators;

public static partial class ElementSemantics
{
    public static string Normalize(string? value) =>
        Whitespace().Replace(value?.Trim() ?? string.Empty, " ");

    /// <summary>
    /// Identifies the title control of the conversation that is currently open.
    ///
    /// A project or folder header carries the same text as the chats filed under
    /// it, so a name match alone is not identity: configuring a project name would
    /// otherwise "verify" whichever unrelated conversation happened to be open and
    /// deliver the instruction there. Containers are told apart by the fact that
    /// they expand and collapse, which the real title controls never do — Claude
    /// exposes its open session as an Invoke "…, rename session" button, and
    /// ChatGPT exposes its open thread as an Invoke header button.
    /// </summary>
    public static bool IsCurrentConversationMarker(
        string? controlType,
        string? name,
        string? className,
        bool supportsExpandCollapse,
        string identifier)
    {
        if (!string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase) || supportsExpandCollapse)
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

    /// <summary>
    /// Identifies the one sidebar row that opens the configured conversation.
    ///
    /// The two apps label their rows differently. ChatGPT rows carry the thread
    /// title verbatim, so they are matched exactly. Claude decorates its rows with
    /// a leading status badge — "Running AgentBridge", "#25 · Open RASTA Bridge" —
    /// so the title is matched as a whole-word suffix instead. Neither form may
    /// expand and collapse, which keeps folder and section headers out.
    ///
    /// Matching stays deliberately narrow because the caller refuses to navigate
    /// unless exactly one row matches; a looser rule would turn a near-miss into a
    /// confident jump to the wrong conversation.
    /// </summary>
    public static bool IsConversationNavigationCandidate(
        string? controlType,
        string? name,
        string? className,
        bool supportsExpandCollapse,
        string identifier)
    {
        if (!string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase) || supportsExpandCollapse)
        {
            return false;
        }

        var normalizedIdentifier = Normalize(identifier);
        if (normalizedIdentifier.Length == 0)
        {
            return false;
        }

        var css = className ?? string.Empty;
        var normalizedName = Normalize(name);

        if (css.Contains("sidebar-item", StringComparison.OrdinalIgnoreCase))
        {
            return !css.Contains("folder-row", StringComparison.OrdinalIgnoreCase)
                && string.Equals(normalizedName, normalizedIdentifier, StringComparison.OrdinalIgnoreCase);
        }

        return css.Contains("df-row", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(normalizedName, normalizedIdentifier, StringComparison.OrdinalIgnoreCase)
                || normalizedName.EndsWith($" {normalizedIdentifier}", StringComparison.OrdinalIgnoreCase));
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
