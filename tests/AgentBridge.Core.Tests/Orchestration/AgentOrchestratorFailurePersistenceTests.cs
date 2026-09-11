using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// The failure paths in this file end a run by calling
/// <c>StopRuntimeResources()</c> — which cancels <c>_runCts</c> — and then
/// writing the Error state and telling the operator about it. Both of those
/// steps ran on <c>_runCts.Token</c>, the very token the line above had just
/// cancelled, because the watchdog probe that reaches these paths is started
/// with that token (see <c>StartCompletionWatchdog</c>).
///
/// Nothing crashed. <c>JsonStateStore.SaveAsync</c> starts by awaiting its file
/// lock with the given token, so an already-cancelled one throws before any
/// write happens; the outer loop's own
/// <c>catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)</c>
/// — written for the ordinary Stop/Pause lifecycle — swallowed it as exactly
/// that. The desktop notifier does the same thing for the same reason:
/// <c>DesktopNotificationService.NotifyAsync</c> checks
/// <c>cancellationToken.IsCancellationRequested</c> and quietly returns. A real
/// run sat in this exact state for hours: the log showed the Error transition,
/// nothing further ran, the state file kept insisting the previous iteration
/// was still in progress, and no notification of any kind went out — the
/// operator found out only by checking by hand.
///
/// <see cref="InMemoryStateStore"/> and <see cref="RecordingNotificationService"/>
/// were changed alongside this fix specifically so a regression here fails a
/// test rather than only a real run: the store now throws on an
/// already-cancelled token exactly where the real one does, and the recorder
/// counts calls that arrived already cancelled instead of accepting them
/// silently.
/// </summary>
public sealed class AgentOrchestratorFailurePersistenceTests
{
    [Fact]
    public async Task AnAgentThatProducedNothingHasItsErrorPersistedAndAnnounced()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness);

        harness.ClaudeAdapter.State.IsProcessing = false;
        await harness.WaitForStateAsync(BridgeState.Error, TimeSpan.FromSeconds(20));

        // The in-memory state machine reaching Error is not what was broken —
        // that always worked. What matters is whether it reached disk: on a
        // restart, only StateStore.Current is read back.
        Assert.NotNull(harness.StateStore.Current);
        Assert.Equal(BridgeState.Error, harness.StateStore.Current!.CurrentState);
        Assert.Contains("produced nothing", harness.StateStore.Current.LastError!, StringComparison.Ordinal);

        // And the operator has to be told, not left to notice on their own.
        Assert.Contains(
            harness.NotificationService.Notifications,
            n => n.Level == NotificationLevel.Error
                && n.Message.Contains("produced nothing", StringComparison.Ordinal));
        Assert.Equal(0, harness.NotificationService.AlreadyCancelledCallCount);
    }

    private static async Task WaitUntilSentAsync(OrchestratorTestHarness harness)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (harness.ClaudeAdapter.State.SendMessageCallCount < 1)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Claude was never invoked.");
            }

            await Task.Delay(20);
        }
    }
}
