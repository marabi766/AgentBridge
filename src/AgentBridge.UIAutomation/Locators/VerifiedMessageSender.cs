using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Locators;

public sealed class VerifiedMessageSender(ILogger<VerifiedMessageSender> logger) : IMessageSender
{
    private static readonly TimeSpan ControlAppearanceTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(12);

    public async Task<bool> SendAsync(
        AutomationElement conversation,
        AutomationElement inputBox,
        string message,
        CancellationToken cancellationToken)
    {
        FlaUI.Core.Patterns.IValuePattern? writablePattern = null;
        var sendInvoked = false;
        if (string.IsNullOrWhiteSpace(message))
        {
            logger.LogWarning("Message delivery refused: the message is empty.");
            return false;
        }

        try
        {
            var valuePattern = inputBox.Patterns.Value.PatternOrDefault;
            if (valuePattern is null || valuePattern.IsReadOnly.ValueOrDefault)
            {
                logger.LogWarning("Message delivery refused: the selected input has no writable Value pattern.");
                return false;
            }
            writablePattern = valuePattern;

            var normalizedMessage = ElementSemantics.Normalize(message);
            var existingDraft = ElementSemantics.Normalize(valuePattern.Value.ValueOrDefault);
            var resumeExactDraft = existingDraft.Length > 0;
            if (resumeExactDraft && !string.Equals(existingDraft, normalizedMessage, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Message delivery refused: the target input contains a different user draft. ExpectedLength={ExpectedLength} ActualLength={ActualLength}.",
                    normalizedMessage.Length, existingDraft.Length);
                return false;
            }

            var receiptCountBefore = CountExactRenderedMessages(conversation, normalizedMessage);
            var window = conversation.AsWindow();
            if (window is null)
            {
                logger.LogWarning("Message delivery refused: the verified conversation root is not a window.");
                return false;
            }

            window.SetForeground();
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (!resumeExactDraft)
            {
                inputBox.Focus();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                if (!inputBox.Properties.HasKeyboardFocus.ValueOrDefault)
                {
                    logger.LogWarning("Message delivery refused: keyboard focus could not be verified on the target input.");
                    return false;
                }

                Keyboard.Type(normalizedMessage);
            }
            else
            {
                logger.LogInformation("Resuming an existing draft that exactly matches the requested message.");
            }
            if (!string.Equals(
                    ElementSemantics.Normalize(valuePattern.Value.ValueOrDefault),
                    normalizedMessage,
                    StringComparison.Ordinal))
            {
                var actual = ElementSemantics.Normalize(valuePattern.Value.ValueOrDefault);
                TryClear(valuePattern);
                logger.LogWarning(
                    "Message delivery refused: the editor draft differed after input. ExpectedLength={ExpectedLength} ActualLength={ActualLength}.",
                    normalizedMessage.Length, actual.Length);
                return false;
            }

            var sendButton = await FindUniqueSendButtonAsync(conversation, cancellationToken).ConfigureAwait(false);
            if (sendButton is null)
            {
                TryClear(valuePattern);
                logger.LogWarning("Message delivery refused: a unique enabled Send button did not appear. Draft was cleared.");
                return false;
            }

            var invoke = sendButton.Patterns.Invoke.PatternOrDefault;
            if (invoke is null)
            {
                TryClear(valuePattern);
                logger.LogWarning("Message delivery refused: the Send control has no Invoke pattern. Draft was cleared.");
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            invoke.Invoke(); // Exactly one irreversible send action.
            sendInvoked = true;

            var deadline = DateTime.UtcNow + ReceiptTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inputCleared = string.IsNullOrWhiteSpace(valuePattern.Value.ValueOrDefault);
                var receiptObserved = CountExactRenderedMessages(conversation, normalizedMessage) > receiptCountBefore;
                if (inputCleared && receiptObserved)
                {
                    logger.LogInformation("Message delivery verified from cleared input and a new exact rendered receipt.");
                    return true;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            logger.LogError("Send was invoked once, but an exact delivery receipt could not be verified before timeout.");
            return false;
        }
        catch (OperationCanceledException)
        {
            if (!sendInvoked && writablePattern is not null)
            {
                TryClear(writablePattern);
            }
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!sendInvoked && writablePattern is not null)
            {
                TryClear(writablePattern);
            }
            logger.LogWarning(ex, "Verified message delivery failed.");
            return false;
        }
    }

    private static async Task<AutomationElement?> FindUniqueSendButtonAsync(
        AutomationElement conversation,
        CancellationToken token)
    {
        var deadline = DateTime.UtcNow + ControlAppearanceTimeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var candidates = conversation.FindAllDescendants()
                .Where(element => Safe(() => element.IsEnabled, false) && !Safe(() => element.IsOffscreen, true))
                .Where(element => ElementSemantics.IsSendButton(
                    Safe(() => element.ControlType.ToString()), Safe(() => element.Name)))
                .Where(element => element.Patterns.Invoke.PatternOrDefault is not null)
                .ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                return null;
            }

            await Task.Delay(100, token).ConfigureAwait(false);
        }

        return null;
    }

    private static int CountExactRenderedMessages(AutomationElement conversation, string normalizedMessage) =>
        conversation.FindAllDescendants().Count(element =>
            ElementSemantics.IsExactRenderedReceipt(Safe(() => element.Name), normalizedMessage));

    private static void TryClear(FlaUI.Core.Patterns.IValuePattern valuePattern)
    {
        try { valuePattern.SetValue(string.Empty); }
        catch { /* best-effort rollback before any send action */ }
    }

    private static T Safe<T>(Func<T> read, T fallback = default!)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
