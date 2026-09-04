using System.ComponentModel;
using FlaUI.Core.AutomationElements;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Locators;

public sealed class VerifiedMessageSender(ILogger<VerifiedMessageSender> logger) : IMessageSender
{
    private static readonly TimeSpan ControlAppearanceTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(3);
    private const int AccessDenied = 5;

    public Task<bool> SendAsync(
        AutomationElement conversation,
        AutomationElement inputBox,
        string message,
        CancellationToken cancellationToken) =>
        SendCoreAsync(conversation, inputBox, message, replaceExistingDraft: false, cancellationToken);

    public Task<bool> SendReplacingDraftAsync(
        AutomationElement conversation,
        AutomationElement inputBox,
        string message,
        CancellationToken cancellationToken) =>
        SendCoreAsync(conversation, inputBox, message, replaceExistingDraft: true, cancellationToken);

    /// <summary>
    /// Waits for the caret to actually land in the target editor.
    ///
    /// Bringing an Electron window forward and moving keyboard focus into its
    /// editor are both asynchronous, and the foreground change can lose a race
    /// with whatever held focus a moment earlier. A single sample taken 100ms
    /// after the click is therefore not evidence that focus will never arrive —
    /// it just means it has not arrived yet. Keep asking until the deadline.
    ///
    /// The guarantee is unchanged: delivery still proceeds only once focus has
    /// been positively observed on the intended input. Only the patience is new.
    /// </summary>
    private static async Task<FocusAttempt> TryFocusInputAsync(
        Window window,
        AutomationElement inputBox,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + FocusTimeout;
        var inputWasDenied = false;
        while (true)
        {
            try
            {
                inputBox.Click();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == AccessDenied)
            {
                // Windows refuses synthetic input aimed at a locked desktop or at
                // a window owned by a higher-privilege process. That condition can
                // clear on its own, so keep trying, but remember why this failed so
                // the refusal can name the real cause instead of reporting it as a
                // focus problem.
                inputWasDenied = true;
            }
            catch (Exception)
            {
                // Any other click failure is retried the same way: the focus check
                // below is what decides, never the click itself.
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            if (inputBox.Properties.HasKeyboardFocus.ValueOrDefault)
            {
                return new FocusAttempt(true, inputWasDenied);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new FocusAttempt(false, inputWasDenied);
            }

            try
            {
                window.SetForeground();
            }
            catch (Exception)
            {
                // The window may briefly refuse the foreground change; the next
                // attempt re-reads focus either way.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly record struct FocusAttempt(bool Focused, bool InputWasDenied);

    private async Task<bool> SendCoreAsync(
        AutomationElement conversation,
        AutomationElement inputBox,
        string message,
        bool replaceExistingDraft,
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
            var rawExistingValue = valuePattern.Value.ValueOrDefault;
            var existingDraft = ElementSemantics.IsEditorPlaceholder(rawExistingValue)
                ? string.Empty
                : ElementSemantics.Normalize(rawExistingValue);
            var resumeExactDraft = existingDraft.Length > 0;
            var draftDiffers = resumeExactDraft
                && !string.Equals(existingDraft, normalizedMessage, StringComparison.Ordinal);
            if (draftDiffers && !replaceExistingDraft)
            {
                logger.LogWarning(
                    "Message delivery refused: the target input contains a different user draft. ExpectedLength={ExpectedLength} ActualLength={ActualLength}.",
                    normalizedMessage.Length, existingDraft.Length);
                return false;
            }

            if (draftDiffers)
            {
                logger.LogWarning(
                    "Replacing the existing editor draft after explicit authorization. ExpectedLength={ExpectedLength} ActualLength={ActualLength}.",
                    normalizedMessage.Length, existingDraft.Length);
                resumeExactDraft = false;
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
                var focus = await TryFocusInputAsync(window, inputBox, cancellationToken).ConfigureAwait(false);
                if (!focus.Focused)
                {
                    logger.LogWarning(
                        focus.InputWasDenied
                            ? "Message delivery refused: Windows denied synthetic input to this desktop. The screen is "
                              + "locked, or a window from a higher-privilege process holds the foreground. Nothing was typed."
                            : "Message delivery refused: keyboard focus could not be verified on the target input.");
                    return false;
                }

                // Electron/Chromium rich-text editors can silently truncate long
                // simulated keystroke sequences. The writable Value pattern is
                // already required and is also what we use to verify/rollback the
                // draft, so set the complete value atomically instead.
                valuePattern.SetValue(normalizedMessage);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
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
                var currentInputValue = valuePattern.Value.ValueOrDefault;
                var inputCleared = string.IsNullOrWhiteSpace(currentInputValue)
                    || ElementSemantics.IsEditorPlaceholder(currentInputValue);
                var receiptCountAfter = CountExactRenderedMessages(conversation, normalizedMessage);
                // Chromium virtualizes older messages, so resending identical text
                // may replace the prior rendered element instead of increasing the
                // visible count. A cleared input after the one Send invocation plus
                // an exact rendered copy is still a verifiable retry receipt.
                var receiptObserved = receiptCountAfter > receiptCountBefore
                    || (receiptCountBefore > 0 && receiptCountAfter > 0);
                var processingObserved = HasActiveProcessingControl(conversation);
                if (inputCleared && (receiptObserved || processingObserved))
                {
                    logger.LogInformation(
                        "Message delivery verified from cleared input and {Evidence}.",
                        receiptObserved ? "an exact rendered receipt" : "an active processing control");
                    return true;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            logger.LogError("Send was invoked once, but neither a rendered receipt nor active processing could be verified before timeout.");
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

    private static bool HasActiveProcessingControl(AutomationElement conversation) =>
        conversation.FindAllDescendants().Any(element =>
            Safe(() => element.IsEnabled, false)
            && !Safe(() => element.IsOffscreen, true)
            && ElementSemantics.IsProcessingButton(
                Safe(() => element.ControlType.ToString()), Safe(() => element.Name)));

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
