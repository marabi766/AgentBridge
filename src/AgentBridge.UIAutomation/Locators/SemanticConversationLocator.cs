using FlaUI.Core.AutomationElements;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Locators;

public sealed class SemanticConversationLocator(ILogger<SemanticConversationLocator> logger) : IConversationLocator
{
    public Task<AutomationElement?> FindConversationAsync(
        AutomationElement mainWindow,
        string? conversationIdentifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(conversationIdentifier))
        {
            logger.LogWarning("Conversation discovery refused: no conversation identifier is configured.");
            return Task.FromResult<AutomationElement?>(null);
        }

        try
        {
            WarmUp(mainWindow, cancellationToken);
            var markers = mainWindow.FindAllDescendants()
                .Where(IsUsable)
                .Where(element => ElementSemantics.IsCurrentConversationMarker(
                    Safe(() => element.ControlType.ToString()), Safe(() => element.Name),
                    Safe(() => element.ClassName), conversationIdentifier))
                .ToArray();

            // Claude exposes the selected session title more than once: a sidebar
            // entry, a plain header button, and one explicit "rename session"
            // header control. The rename control is the strongest current-session
            // identity marker, so prefer its unique match over weaker duplicates.
            var preferredMarkers = markers
                .Where(element => ElementSemantics.IsPreferredCurrentConversationMarker(
                    Safe(() => element.ControlType.ToString()), Safe(() => element.Name),
                    conversationIdentifier))
                .ToArray();

            if (preferredMarkers.Length == 1)
            {
                return Task.FromResult<AutomationElement?>(mainWindow);
            }

            if (markers.Length != 1)
            {
                logger.LogWarning(
                    "Conversation discovery refused: expected one current-title marker for '{Identifier}', found {Count}.",
                    conversationIdentifier, markers.Length);
                return Task.FromResult<AutomationElement?>(null);
            }

            // The verified current-title marker establishes the identity of the open
            // conversation. Return the window so later selectors remain in that tree.
            return Task.FromResult<AutomationElement?>(mainWindow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Conversation discovery failed.");
            return Task.FromResult<AutomationElement?>(null);
        }
    }

    private static void WarmUp(AutomationElement root, CancellationToken token)
    {
        var firstRead = root.FindAllDescendants();
        if (firstRead.Length < 20)
        {
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            token.ThrowIfCancellationRequested();
        }
    }

    private static bool IsUsable(AutomationElement element) =>
        Safe(() => element.IsEnabled, false) && !Safe(() => element.IsOffscreen, true);

    private static T Safe<T>(Func<T> read, T fallback = default!)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
