using System.ComponentModel;
using FlaUI.Core.AutomationElements;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Locators;

public sealed class VerifiedMessageSender(ILogger<VerifiedMessageSender> logger) : IMessageSender
{
    private static readonly TimeSpan ControlAppearanceTimeout = TimeSpan.FromSeconds(3);
    // Generous on purpose. Send has already happened by the time this is being
    // waited on, so giving up early does not undo anything — it only turns a
    // delivery that worked into a reported failure, which invites a human to
    // resend and say the same thing twice. A throttled Chromium renderer on a
    // locked desktop was observed taking well over twenty seconds to publish the
    // sent message to its accessibility tree.
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(3);
    // How long the editor may take to report back the value that was set. On a
    // disconnected desktop session the accessibility tree lags far behind the
    // write: a 911 character instruction was still reading back as 11 characters
    // eight seconds in, and was present in full once the tree caught up. Waiting
    // costs nothing but time; giving up early discards a draft that did land.
    private static readonly TimeSpan DraftSettleTimeout = TimeSpan.FromSeconds(60);
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
    /// Focus is asked for two different ways, weakest side effect first.
    /// UI Automation's own SetFocus is a call into the target's provider rather
    /// than synthetic input, so it neither moves the operator's mouse nor needs an
    /// interactive desktop — it is the reason delivery works while Windows is
    /// locked, where SendInput is refused outright. A real click stays as the
    /// fallback for Chromium builds that only move the caret on one.
    ///
    /// Moving focus is also asynchronous and can lose a race with whatever held it
    /// a moment earlier, so a single sample proves nothing except that focus has
    /// not arrived yet. Keep asking until the deadline.
    ///
    /// The guarantee is unchanged: delivery still proceeds only once focus has
    /// been positively observed on the intended input.
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
            Attempt(inputBox.Focus);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            if (inputBox.Properties.HasKeyboardFocus.ValueOrDefault)
            {
                return new FocusAttempt(true, inputWasDenied);
            }

            try
            {
                inputBox.Click();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == AccessDenied)
            {
                // Windows refuses synthetic input aimed at a locked desktop or at
                // a window owned by a higher-privilege process. Remember why, so a
                // refusal can name the real cause rather than blaming focus, and
                // keep going: SetFocus above may still carry the delivery.
                inputWasDenied = true;
            }
            catch (Exception)
            {
                // Any other click failure is retried the same way: the focus check
                // is what decides, never the click itself.
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

            Attempt(window.SetForeground);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for the editor to finish applying the value that was just set.
    ///
    /// Setting the value returns before the editor has rendered it, and how long
    /// that takes is not fixed: a Chromium renderer on a locked or hidden desktop
    /// is throttled, and a long instruction can still be arriving hundreds of
    /// milliseconds later. Reading once on a fixed delay turns that into a false
    /// mismatch — observed live as a 911 character message reading back as 11,
    /// being cleared as corrupt, and then landing in full anyway.
    ///
    /// Returns as soon as the draft matches, so the common case stays fast. The
    /// caller re-reads and remains the one that decides; this only stops it from
    /// judging a draft that is still being written.
    /// </summary>
    private static async Task SettleDraftAsync(
        AutomationElement conversation,
        FlaUI.Core.Patterns.IValuePattern valuePattern,
        string expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + DraftSettleTimeout;
        while (true)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var current = ElementSemantics.Normalize(ReadDraft(conversation, valuePattern));
            if (string.Equals(current, expected, StringComparison.Ordinal) || DateTime.UtcNow >= deadline)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads what the editor currently holds, from an element located right now.
    ///
    /// Setting a value makes these editors rebuild the node behind the composer,
    /// which leaves the element the delivery started from answering with a stale
    /// value indefinitely. That was observed all evening as a 911 character
    /// instruction reading back as 11 while the composer visibly held the whole
    /// thing — on a locked desktop, on a disconnected one, and on a live one
    /// alike, which is what ruled out every timing explanation. Re-finding the
    /// input is what makes the readback describe the editor rather than a node
    /// the editor has already discarded.
    ///
    /// Falls back to the original pattern when no input can be located, so a
    /// window mid-render degrades to the old behaviour instead of throwing.
    /// </summary>
    private static string ReadDraft(
        AutomationElement conversation,
        FlaUI.Core.Patterns.IValuePattern original)
    {
        try
        {
            var live = conversation.FindAllDescendants().FirstOrDefault(element =>
                ElementSemantics.IsInputCandidate(
                    Safe(() => element.ControlType.ToString()),
                    Safe(() => element.Name),
                    Safe(() => element.ClassName)));
            var pattern = live?.Patterns.Value.PatternOrDefault;
            if (pattern is not null)
            {
                return pattern.Value.ValueOrDefault ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // Fall through to the original reference below.
        }

        return original.Value.ValueOrDefault ?? string.Empty;
    }

    /// <summary>Runs a best-effort focus nudge; the focus check is the verdict.</summary>
    private static void Attempt(Action action)
    {
        try { action(); }
        catch (Exception) { /* A locked desktop refuses some of these outright. */ }
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

            // Best effort only: a locked desktop has no foreground to give, and
            // delivery no longer depends on getting one.
            Attempt(window.SetForeground);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);

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

            if (resumeExactDraft)
            {
                logger.LogInformation(
                    "The editor already shows this exact message. Writing it again rather than sending what is "
                    + "there: an identical draft is normally the residue of an attempt that failed, and its text "
                    + "may exist only in the accessibility layer while the editor itself holds nothing.");
            }

            // Electron/Chromium rich-text editors can silently truncate long
            // simulated keystroke sequences. The writable Value pattern is
            // already required and is also what we use to verify/rollback the
            // draft, so set the complete value atomically instead.
            //
            // This runs even when the draft already matches. Skipping it once cost
            // a delivery: Send was pressed on a leftover draft, the composer
            // emptied, and nothing was ever posted — the visible text was a
            // remnant the editor no longer had. Rewriting identical text changes
            // nothing the operator can see and puts the editor in a state this
            // code established.
            valuePattern.SetValue(normalizedMessage);
            await SettleDraftAsync(conversation, valuePattern, normalizedMessage, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    ElementSemantics.Normalize(ReadDraft(conversation, valuePattern)),
                    normalizedMessage,
                    StringComparison.Ordinal))
            {
                var actual = ElementSemantics.Normalize(ReadDraft(conversation, valuePattern));
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
                var currentInputValue = ReadDraft(conversation, valuePattern);
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

            logger.LogError(
                "Send was invoked once, but neither a rendered receipt nor active processing could be verified "
                + "within {Timeout}. The message may well have been delivered — check the conversation before "
                + "resending, or acknowledge the delivery, so the same instruction is not sent twice.",
                ReceiptTimeout);
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
