using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using AgentBridge.Fakes;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// What ended a real overnight run: the instant an allowance reset, the first
/// invocation came back "403 Request not allowed" in under a second, from an
/// authentication state that had not caught up. By the time anyone looked,
/// nothing was wrong — but the loop had already stopped.
///
/// An agent that stops before it could have started is not an empty iteration.
/// It is a refusal that usually fixes itself, and it is worth one more attempt.
/// </summary>
public sealed class FailedRunRetryTests
{
    [Fact]
    public async Task AnAgentThatStoppedBeforeItCouldWorkIsAskedAgainRatherThanEndingTheRun()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness.ClaudeAdapter, 1);

        // It stops without writing anything, and says it never got started.
        harness.ClaudeAdapter.State.LastRunFailedWithoutWorking = true;
        harness.ClaudeAdapter.State.IsProcessing = false;

        // The same instruction goes out again instead of the run ending.
        await WaitUntilSentAsync(harness.ClaudeAdapter, 2, TimeSpan.FromSeconds(90));
        Assert.Equal(harness.ClaudeAdapter.State.SentMessages[0], harness.ClaudeAdapter.State.SentMessages[1]);
    }

    [Fact]
    public async Task RetryingIsBoundedSoAWallThatDoesNotMoveStillEndsTheRun()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;
        harness.ClaudeAdapter.State.LastRunFailedWithoutWorking = true;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");

        // Every invocation fails the same way. Left unbounded this would resend
        // forever; the run has to conclude that the failure is real.
        using var keepFailing = new CancellationTokenSource();
        var failing = Task.Run(async () =>
        {
            while (!keepFailing.IsCancellationRequested)
            {
                if (harness.ClaudeAdapter.State.IsProcessing)
                {
                    await Task.Delay(200);
                    harness.ClaudeAdapter.State.IsProcessing = false;
                }

                await Task.Delay(50);
            }
        });

        var status = await harness.WaitForStateAsync(BridgeState.Error, TimeSpan.FromSeconds(180));
        Assert.Contains("produced nothing", status.LastError!, StringComparison.Ordinal);
        await keepFailing.CancelAsync();
        await failing;
    }

    [Fact]
    public async Task AnAgentThatWorkedAndStillProducedNothingIsNotRetried()
    {
        // The distinction the whole change rests on. This one ran long enough to
        // have done the work and did not — a permission it could not be granted,
        // a task it gave up on. Resending would repeat it, so the run stops and
        // says so.
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness.ClaudeAdapter, 1);

        harness.ClaudeAdapter.State.LastRunFailedWithoutWorking = false;
        harness.ClaudeAdapter.State.IsProcessing = false;

        var status = await harness.WaitForStateAsync(BridgeState.Error, TimeSpan.FromSeconds(30));
        Assert.Contains("produced nothing", status.LastError!, StringComparison.Ordinal);
        Assert.Equal(1, harness.ClaudeAdapter.State.SendMessageCallCount);
    }

    private static Task StartAtCodexCheckpointAsync(OrchestratorTestHarness harness) =>
        harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);

    private static async Task WaitUntilSentAsync(
        FakeAgentAdapterBase adapter, int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (adapter.State.SendMessageCallCount >= count)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"{adapter.Name} was sent {adapter.State.SendMessageCallCount} instructions, expected {count}.");
    }
}
