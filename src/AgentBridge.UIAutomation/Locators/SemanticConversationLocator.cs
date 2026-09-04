using FlaUI.Core.AutomationElements;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Locators;

public sealed class SemanticConversationLocator(ILogger<SemanticConversationLocator> logger) : IConversationLocator
{
    public async Task<AutomationElement?> FindConversationAsync(
        AutomationElement mainWindow,
        string? conversationIdentifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(conversationIdentifier))
        {
            logger.LogWarning("Conversation discovery refused: no conversation identifier is configured.");
            return null;
        }

        try
        {
            WarmUp(mainWindow, cancellationToken);
            var markers = FindCurrentMarkers(mainWindow, conversationIdentifier);

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
                return mainWindow;
            }

            if (markers.Length == 1)
            {
                return mainWindow;
            }

            if (markers.Length > 1)
            {
                logger.LogWarning(
                    "Conversation discovery refused: expected one current-title marker for '{Identifier}', found {Count}.",
                    conversationIdentifier, markers.Length);
                return null;
            }

            var navigationCandidates = mainWindow.FindAllDescendants()
                .Where(IsUsable)
                .Where(element => ElementSemantics.IsConversationNavigationCandidate(
                    Safe(() => element.ControlType.ToString()), Safe(() => element.Name),
                    Safe(() => element.ClassName),
                    Safe(() => element.Patterns.ExpandCollapse.IsSupported, false),
                    conversationIdentifier))
                .ToArray();
            if (navigationCandidates.Length != 1)
            {
                logger.LogWarning(
                    "Conversation discovery refused: no current marker and expected one sidebar target for '{Identifier}', found {Count}.",
                    conversationIdentifier, navigationCandidates.Length);
                return null;
            }

            navigationCandidates[0].Click();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                markers = FindCurrentMarkers(mainWindow, conversationIdentifier);
                preferredMarkers = markers
                    .Where(element => ElementSemantics.IsPreferredCurrentConversationMarker(
                        Safe(() => element.ControlType.ToString()), Safe(() => element.Name),
                        conversationIdentifier))
                    .ToArray();
                if (preferredMarkers.Length == 1 || markers.Length == 1)
                {
                    logger.LogInformation("Opened and verified configured conversation '{Identifier}'.", conversationIdentifier);
                    return mainWindow;
                }
            }

            logger.LogWarning("Conversation navigation did not produce a verified current marker for '{Identifier}'.", conversationIdentifier);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Conversation discovery failed.");
            return null;
        }
    }

    private static AutomationElement[] FindCurrentMarkers(AutomationElement mainWindow, string conversationIdentifier) =>
        mainWindow.FindAllDescendants()
            .Where(IsUsable)
            .Where(element => ElementSemantics.IsCurrentConversationMarker(
                Safe(() => element.ControlType.ToString()), Safe(() => element.Name),
                Safe(() => element.ClassName),
                Safe(() => element.Patterns.ExpandCollapse.IsSupported, false),
                conversationIdentifier))
            .ToArray();

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
