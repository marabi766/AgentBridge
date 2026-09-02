using FlaUI.Core.AutomationElements;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Locators;

public sealed class SemanticInputLocator(ILogger<SemanticInputLocator> logger) : IInputLocator
{
    public Task<AutomationElement?> FindInputBoxAsync(AutomationElement conversation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var candidates = conversation.FindAllDescendants()
                .Where(element => Safe(() => element.IsEnabled, false) && !Safe(() => element.IsOffscreen, true))
                .Where(element => ElementSemantics.IsInputCandidate(
                    Safe(() => element.ControlType.ToString()), Safe(() => element.Name), Safe(() => element.ClassName)))
                .Where(element => element.Patterns.Value.PatternOrDefault is not null)
                .ToArray();

            if (candidates.Length != 1)
            {
                logger.LogWarning("Input discovery refused: expected one usable semantic input, found {Count}.", candidates.Length);
                return Task.FromResult<AutomationElement?>(null);
            }

            return Task.FromResult<AutomationElement?>(candidates[0]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Input discovery failed.");
            return Task.FromResult<AutomationElement?>(null);
        }
    }

    private static T Safe<T>(Func<T> read, T fallback = default!)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
