using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// A remote control session is opened beside the run, not inside it. Everything
/// here is about that distinction: the operator gets a link, and the cycle they
/// are watching carries on exactly as it was.
/// </summary>
public sealed class RemoteControlTests
{
    private static readonly RemoteControlSession Opened = new()
    {
        SessionId = "b627a19b",
        Url = "https://claude.ai/code/session_017ZtM7YFUZujzPtfHAGo75P",
        OpenedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task TheLinkIsReturnedAndSentToWhateverChannelsAreConfigured()
    {
        // The link is for another device, and the notification channels are how
        // this run already reaches one — leaving it only on screen would mean
        // typing a session URL off a monitor by hand.
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.RemoteControlSession = Opened;

        var session = await harness.Orchestrator.OpenClaudeRemoteControlAsync(CancellationToken.None);

        Assert.Equal(Opened.Url, session!.Url);
        Assert.Contains(
            harness.NotificationService.Notifications,
            n => n.Message.Contains(Opened.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpeningASessionDoesNotMoveTheCycle()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;
        harness.ClaudeAdapter.State.RemoteControlSession = Opened;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);

        var before = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        await harness.Orchestrator.OpenClaudeRemoteControlAsync(CancellationToken.None);
        var after = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);

        Assert.Equal(before.CurrentState, after.CurrentState);
        Assert.Equal(before.CurrentIteration, after.CurrentIteration);

        // And nothing was delivered to the agent: the session is a copy opened
        // alongside, not another instruction sent into the run.
        Assert.Equal(1, harness.ClaudeAdapter.State.SendMessageCallCount);
    }

    [Fact]
    public async Task ASessionCanBeOpenedWhileTheAgentIsStillWorking()
    {
        // Unlike Continue, this needs no idle agent. Mid-run is exactly when an
        // operator walks away from the desk and wants it.
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.BecomesBusyOnSend = true;
        harness.ClaudeAdapter.State.RemoteControlSession = Opened;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness, 1);
        Assert.True(await harness.ClaudeAdapter.IsProcessingAsync(CancellationToken.None));

        var session = await harness.Orchestrator.OpenClaudeRemoteControlAsync(CancellationToken.None);

        Assert.NotNull(session);
    }

    [Fact]
    public async Task ASessionThatCouldNotBeOpenedIsReportedRatherThanInvented()
    {
        // Null means the agent published no link. Returning a made-up one, or
        // claiming success, would send the operator to a page that is not there.
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.RemoteControlSession = null;

        var session = await harness.Orchestrator.OpenClaudeRemoteControlAsync(CancellationToken.None);

        Assert.Null(session);
        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Contains("did not return", status.LastAction!, StringComparison.OrdinalIgnoreCase);
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
