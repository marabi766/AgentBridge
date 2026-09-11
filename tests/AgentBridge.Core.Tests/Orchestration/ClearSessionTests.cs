using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// The bridge's own <c>/clear</c>. Session reuse (see
/// <see cref="SessionReuseTests"/>) exists to skip the expensive re-exploration
/// a fresh command line run pays for — but the conversation it reuses only
/// grows, and an operator watching it grow should be able to reset that without
/// waiting for the next <c>FreshSessionEveryIterations</c> boundary.
///
/// Claude only. Codex's automatic reuse is already permanently off, so there is
/// no equivalent flag for a Codex version of this to discard; a "Clear Codex
/// Session" was shipped once and, having nothing to actually clear, only
/// updated status text — reporting an action that had not happened — and was
/// removed rather than kept as a placebo.
/// </summary>
public sealed class ClearSessionTests
{
    [Fact]
    public async Task ClearingClaudesSessionMakesItsNextOrdinaryDeliveryFreshEvenThoughItWouldOtherwiseResume()
    {
        // No fresh-interval boundary in play — without Clear, iteration 2 would
        // resume exactly as SessionReuseTests.LaterIterationsGoIntoTheSessionTheAgentAlreadyHas
        // demonstrates. Clear is the only reason it should not this time.
        var harness = new OrchestratorTestHarness(Config(reuse: true));
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("first task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);
        Assert.Empty(harness.ClaudeAdapter.State.ResumedMessages);

        await harness.Orchestrator.ClearClaudeSessionAsync(CancellationToken.None);

        harness.ClaudeAdapter.State.IsProcessing = false;
        harness.ClaudeWatcher.RaiseStableChange("first report", "claude-hash-1");
        harness.CodexWatcher.RaiseStableChange("second task", "codex-hash-2");
        await WaitUntilSentAsync(harness, 2);

        Assert.Empty(harness.ClaudeAdapter.State.ResumedMessages);
    }

    [Fact]
    public async Task ClearingBeforeAnySessionExistsDoesNothingHarmful()
    {
        var harness = new OrchestratorTestHarness(Config(reuse: true));

        await harness.Orchestrator.ClearClaudeSessionAsync(CancellationToken.None);

        // The very first delivery was always going to start fresh regardless;
        // this only confirms Clear did not leave anything in a state that
        // stops that first delivery from happening at all.
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;
        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("first task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        Assert.Empty(harness.ClaudeAdapter.State.ResumedMessages);
    }

    /// <summary>The harness defaults, with only the setting under test moved.</summary>
    private static BridgeConfiguration Config(bool reuse) =>
        BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = false,
            MaximumIterations = 50,
            AgentTimeoutSeconds = 2,
            RetryCount = 0,
            NotificationsEnabled = true,
            ReuseAgentSessions = reuse,
        };

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
