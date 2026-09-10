using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using AgentBridge.Fakes;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// The message an operator who has walked away actually wants: "iteration N is
/// done, here is what it says". The report has to travel with the notification,
/// because a channel like Telegram is the whole point of wanting one.
/// </summary>
public sealed class AgentFinishedNotificationTests
{
    [Fact]
    public async Task WhenClaudeDeliversItsReportTheNotificationCarriesTheFileContent()
    {
        var harness = new OrchestratorTestHarness();

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await WaitUntilSentAsync(harness.ClaudeAdapter, 1);

        const string report = "# Iteration report\n- changed the projector\n- tests green";
        harness.ClaudeAdapter.State.IsProcessing = false;
        harness.ClaudeWatcher.RaiseStableChange(report, "claude-hash-1");
        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt, TimeSpan.FromSeconds(30));

        var finished = harness.NotificationService.Notifications
            .Single(n => n.Title.Contains("Claude finished", StringComparison.Ordinal));
        Assert.NotNull(finished.Attachment);
        Assert.Equal(report, finished.Attachment!.Content);
        Assert.EndsWith(".md", finished.Attachment.FileName);
    }

    [Fact]
    public async Task WhenCodexDeliversItsPromptTheNotificationCarriesThatFile()
    {
        var harness = new OrchestratorTestHarness();

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForClaudeReport, CancellationToken.None);
        harness.ClaudeWatcher.RaiseStableChange("a report", "claude-hash-1");
        await WaitUntilSentAsync(harness.CodexAdapter, 1);

        const string prompt = "Next: wire the DLQ route and add the replay test.";
        harness.CodexAdapter.State.IsProcessing = false;
        harness.CodexWatcher.RaiseStableChange(prompt, "codex-hash-1");
        await harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport, TimeSpan.FromSeconds(30));

        var finished = harness.NotificationService.Notifications
            .Single(n => n.Title.Contains("Codex finished", StringComparison.Ordinal));
        Assert.Equal(prompt, finished.Attachment!.Content);
    }

    private static async Task WaitUntilSentAsync(FakeAgentAdapterBase adapter, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (adapter.State.SendMessageCallCount >= count)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"{adapter.Name} was never sent an instruction.");
    }
}
