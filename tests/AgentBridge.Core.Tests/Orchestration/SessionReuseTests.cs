using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// A fresh command line run remembers nothing, so every iteration re-reads the
/// project and re-explores the repository before it starts work. Those turns are
/// the expensive part of a run — measured runs spent sixty-five of them — and
/// reusing the session skips them.
///
/// The cost of reusing is a conversation that only grows, so these tests are
/// mostly about the limits: never before there is a session, and not forever.
/// </summary>
public sealed class SessionReuseTests
{
    [Fact]
    public async Task TheFirstDeliveryStartsASessionRatherThanResumingOne()
    {
        // There is nothing to resume yet. Asking a CLI to continue a
        // conversation that does not exist fails the run outright, and a fresh
        // project is exactly that case.
        var harness = new OrchestratorTestHarness(Config(reuse: true));
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        Assert.Empty(harness.ClaudeAdapter.State.ResumedMessages);
    }

    [Fact]
    public async Task LaterIterationsGoIntoTheSessionTheAgentAlreadyHas()
    {
        var harness = new OrchestratorTestHarness(Config(reuse: true));
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("first task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        // Round the cycle: Claude reports, Codex reviews, Codex writes the next
        // prompt. Only then is Claude asked for anything a second time.
        harness.ClaudeAdapter.State.IsProcessing = false;
        harness.ClaudeWatcher.RaiseStableChange("first report", "claude-hash-1");
        harness.CodexWatcher.RaiseStableChange("second task", "codex-hash-2");
        await WaitUntilSentAsync(harness, 2);

        // The second instruction is the full template, not a nudge — a new task
        // still has to be stated — but it lands in the existing conversation.
        var second = harness.ClaudeAdapter.State.SentMessages[1];
        Assert.Contains("iteration", second, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([second], harness.ClaudeAdapter.State.ResumedMessages);
    }

    [Fact]
    public async Task WithReuseOffEveryIterationStartsClean()
    {
        var harness = new OrchestratorTestHarness(Config(reuse: false));
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("first task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        harness.ClaudeAdapter.State.IsProcessing = false;
        harness.ClaudeWatcher.RaiseStableChange("first report", "claude-hash-1");
        harness.CodexWatcher.RaiseStableChange("second task", "codex-hash-2");
        await WaitUntilSentAsync(harness, 2);

        Assert.Empty(harness.ClaudeAdapter.State.ResumedMessages);
    }

    [Fact]
    public async Task TheSessionIsStartedAfreshOnTheChosenInterval()
    {
        // Without this a run of a hundred iterations carries every dead end from
        // the first one into the last. Iteration 2 with an interval of 2 is the
        // first multiple, so it starts clean.
        var harness = new OrchestratorTestHarness(Config(reuse: true, freshEvery: 2));
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("first task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        harness.ClaudeAdapter.State.IsProcessing = false;
        harness.ClaudeWatcher.RaiseStableChange("first report", "claude-hash-1");
        harness.CodexWatcher.RaiseStableChange("second task", "codex-hash-2");
        await WaitUntilSentAsync(harness, 2);

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(2, status.CurrentIteration);
        Assert.Empty(harness.ClaudeAdapter.State.ResumedMessages);
    }

    /// <summary>The harness defaults, with only the setting under test moved.</summary>
    private static BridgeConfiguration Config(bool reuse, int freshEvery = 0) =>
        BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = false,
            MaximumIterations = 50,
            AgentTimeoutSeconds = 2,
            RetryCount = 0,
            NotificationsEnabled = true,
            ReuseAgentSessions = reuse,
            FreshSessionEveryIterations = freshEvery,
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
