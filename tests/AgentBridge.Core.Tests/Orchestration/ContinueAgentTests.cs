using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// Continue is for the case no rule can decide: an agent that ran, did real
/// work, and stopped before finishing. Whether that work is worth carrying on is
/// a judgement about the repository, so it stays a button — but what the button
/// sends has to be right, or it is worse than nothing.
/// </summary>
public sealed class ContinueAgentTests
{
    [Fact]
    public async Task ContinuingSendsANudgeIntoTheExistingSessionRatherThanTheInstructionAgain()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        // It stops without writing its report — the run that is worth continuing.
        harness.ClaudeAdapter.State.IsProcessing = false;
        await harness.Orchestrator.ContinueClaudeAsync(CancellationToken.None);

        var messages = harness.ClaudeAdapter.State.SentMessages;
        Assert.Equal(2, messages.Count);

        // The first was the whole instruction; the second is one word, because
        // the session it lands in already holds everything the first one said.
        Assert.Contains("iteration", messages[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("continue", messages[1]);

        // And it went into the existing session, not a fresh one. Without this
        // the word arrives at an agent with no idea what to continue.
        Assert.Equal(["continue"], harness.ClaudeAdapter.State.ResumedMessages);
    }

    [Fact]
    public async Task ContinuingDoesNotAdvanceTheIteration()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);
        var before = (await harness.Orchestrator.GetStatusAsync(CancellationToken.None)).CurrentIteration;

        harness.ClaudeAdapter.State.IsProcessing = false;
        await harness.Orchestrator.ContinueClaudeAsync(CancellationToken.None);

        var after = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(before, after.CurrentIteration);
        Assert.Equal(BridgeState.WaitingForClaudeReport, after.CurrentState);
    }

    [Fact]
    public async Task AnAgentThatIsStillWorkingCannotBeContinued()
    {
        // There is nothing to continue while it is still going, and starting a
        // second run underneath the first is how two agents end up editing the
        // same files at once.
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Orchestrator.ContinueClaudeAsync(CancellationToken.None));
        Assert.Contains("still working", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.ClaudeAdapter.State.SendMessageCallCount);
    }

    [Fact]
    public async Task ClaudeCanBeContinuedOutOfTheErrorItsOwnEmptyRunCaused()
    {
        // "Claude finished without updating its report" is the error most likely
        // to be worth continuing from. Until WaitingForClaude was reachable from
        // Error, it was the one error Claude could not be continued out of.
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        harness.ClaudeAdapter.State.LastRunFailedWithoutWorking = false;
        harness.ClaudeAdapter.State.IsProcessing = false;
        await harness.WaitForStateAsync(BridgeState.Error, TimeSpan.FromSeconds(30));

        await harness.Orchestrator.ContinueClaudeAsync(CancellationToken.None);

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Null(status.LastError);
        Assert.Equal(["continue"], harness.ClaudeAdapter.State.ResumedMessages);
    }

    private static async Task WaitUntilSentAsync(OrchestratorTestHarness harness, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (harness.ClaudeAdapter.State.SendMessageCallCount >= count)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Claude was sent {harness.ClaudeAdapter.State.SendMessageCallCount} instructions, expected {count}.");
    }
}
