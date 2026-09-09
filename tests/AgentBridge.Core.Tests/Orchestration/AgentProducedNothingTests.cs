using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using AgentBridge.Fakes;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// The case a real unattended run sat in for nearly eight hours: Claude was
/// invoked, worked for twenty-four minutes, exited cleanly, and never rewrote
/// its report. Both waits in this bridge are passive, so nothing was left to end
/// the wait — the run simply stopped making progress without saying so.
/// </summary>
public sealed class AgentProducedNothingTests
{
    [Fact]
    public async Task AnAgentThatStopsWithoutWritingItsFileEndsTheRunInError()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilInvokedAsync(harness.ClaudeAdapter);

        // Claude stops. Its report file never changes, which is the whole point:
        // there is no event coming that could move the run along.
        harness.ClaudeAdapter.State.IsProcessing = false;

        var status = await harness.WaitForStateAsync(BridgeState.Error, TimeSpan.FromSeconds(20));
        Assert.Contains("Claude", status.LastError!, StringComparison.Ordinal);
        Assert.Contains(harness.Configuration.ClaudeReportFileName, status.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameHoldsForCodex()
    {
        var harness = new OrchestratorTestHarness();
        harness.CodexAdapter.State.BecomesBusyOnSend = true;

        await harness.StartAsync();
        harness.ClaudeWatcher.RaiseStableChange("a report", "claude-hash-1");
        await WaitUntilInvokedAsync(harness.CodexAdapter);

        harness.CodexAdapter.State.IsProcessing = false;

        var status = await harness.WaitForStateAsync(BridgeState.Error, TimeSpan.FromSeconds(20));
        Assert.Contains("Codex", status.LastError!, StringComparison.Ordinal);
        Assert.Contains(harness.Configuration.CodexPromptFileName, status.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentThatDoesWriteItsFileIsNotAccusedOfProducingNothing()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilInvokedAsync(harness.ClaudeAdapter);

        // The report is waiting to be found the moment Claude stops — exactly what
        // the post-completion recheck exists to pick up.
        harness.ClaudeWatcher.ArrangeCheckNowResult("the report", "claude-hash-1");
        harness.ClaudeAdapter.State.IsProcessing = false;

        // The run carries on to Codex rather than failing.
        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task AnAgentThatWasNeverSeenWorkingIsNotJudged()
    {
        // A desktop agent can look idle for a moment after an instruction lands,
        // before it renders anything that says it is busy. Calling that a failed
        // iteration would end runs that were about to succeed, so the verdict
        // requires having actually seen the agent work first.
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = false;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilInvokedAsync(harness.ClaudeAdapter);

        await Task.Delay(TimeSpan.FromSeconds(8));

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task ADryRunIsNotJudgedEither()
    {
        // Nothing was actually invoked, so there is no agent whose silence could
        // mean anything.
        var harness = new OrchestratorTestHarness(BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = true,
            MaximumIterations = 50,
            AgentTimeoutSeconds = 2,
            RetryCount = 0,
        });
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");

        // A dry run sends nothing, so the only evidence it advanced is the state.
        await harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport);
        await Task.Delay(TimeSpan.FromSeconds(8));

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task AnExhaustedAllowanceIsWaitedOutAndTheInstructionResent()
    {
        // The point of the whole mechanism: a refusal is a delay, not a fault, so
        // the run must neither fail nor sit there — it resends once the agent has
        // something to spend again.
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilInvokedAsync(harness.ClaudeAdapter);

        // Claude stops with nothing done, and reports why.
        harness.ClaudeAdapter.State.Status = AgentStatus.RateLimited;
        harness.ClaudeAdapter.State.IsProcessing = false;

        await WaitForActionAsync(harness, "no allowance left");
        Assert.Contains(
            harness.NotificationService.Notifications,
            notification => notification.Message.Contains("resumes by itself", StringComparison.Ordinal));

        // The allowance comes back.
        harness.ClaudeAdapter.State.Status = AgentStatus.Ready;

        await WaitUntilInvokedAsync(harness.ClaudeAdapter, expectedCount: 2);
        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Null(status.LastError);
        Assert.NotEqual(BridgeState.Error, status.CurrentState);
    }

    [Fact]
    public async Task AnAllowanceThatNeverRecoversStopsInsteadOfResendingForever()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await StartAtCodexCheckpointAsync(harness);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");

        // Every resend is refused the same way: busy for a moment, then out of
        // allowance again, having produced nothing. The refusal is held longer
        // than the probe's one-second poll, or the probe could step straight over
        // it and report the iteration as merely empty.
        using var stopRefusing = new CancellationTokenSource();
        var exhausted = Task.Run(async () =>
        {
            while (!stopRefusing.IsCancellationRequested)
            {
                if (harness.ClaudeAdapter.State.IsProcessing)
                {
                    harness.ClaudeAdapter.State.Status = AgentStatus.RateLimited;
                    harness.ClaudeAdapter.State.IsProcessing = false;
                    await Task.Delay(TimeSpan.FromSeconds(2.5));
                    harness.ClaudeAdapter.State.Status = AgentStatus.Ready;
                }

                await Task.Delay(50);
            }
        });

        var status = await harness.WaitForStateAsync(BridgeState.Error, TimeSpan.FromSeconds(120));
        Assert.Contains("out of allowance", status.LastError!, StringComparison.Ordinal);
        await stopRefusing.CancelAsync();
        await exhausted;
    }

    private static async Task WaitForActionAsync(OrchestratorTestHarness harness, string fragment)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
            if (status.LastAction?.Contains(fragment, StringComparison.Ordinal) == true)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"No status action ever mentioned '{fragment}'.");
    }

    /// <summary>
    /// Claude is only ever invoked from the Codex checkpoint. A fresh Start waits
    /// for a Claude report instead, and a Codex prompt arriving there is
    /// deliberately ignored as out of order — so these tests have to start on the
    /// side they are about.
    /// </summary>
    private static Task StartAtCodexCheckpointAsync(OrchestratorTestHarness harness) =>
        harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);

    /// <summary>
    /// Waits until the agent has actually been handed an instruction. Waiting on
    /// a state instead would not do: several of these states are also the state a
    /// Start lands in, so they say nothing about whether the agent was invoked.
    /// </summary>
    private static async Task WaitUntilInvokedAsync(FakeAgentAdapterBase adapter, int expectedCount = 1)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (adapter.State.SentMessages.Count < expectedCount)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"{adapter.Name} was given {adapter.State.SentMessages.Count} instruction(s), expected {expectedCount}.");
            }

            await Task.Delay(10);
        }
    }
}
