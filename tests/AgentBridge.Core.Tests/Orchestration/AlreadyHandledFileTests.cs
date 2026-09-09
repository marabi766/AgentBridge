using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// The dead end a real run sat in three times in one morning: a checkpoint was
/// chosen, the file it waits for was already recorded as handled, and the bridge
/// waited for a revision that could never arrive — the other agent writes its
/// file only in answer to this one.
///
/// The skip itself is correct. What was wrong was that it happened at debug
/// level, which neither host emits: nothing on screen, nothing in the log, and
/// no way to tell a dead end from an agent still thinking except by reading the
/// persisted hashes by hand.
/// </summary>
public sealed class AlreadyHandledFileTests
{
    [Fact]
    public async Task AReportIdenticalToTheOneAlreadyHandledSaysSoAndSaysWhatFixesIt()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        // Handled once: the run moves on to Codex.
        harness.ClaudeWatcher.RaiseStableChange("a report", "claude-hash-1");
        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt, TimeSpan.FromSeconds(20));

        // Now the same content arrives again — a restart's catch-up re-reading a
        // file nobody has rewritten.
        await harness.Orchestrator.StopAsync(CancellationToken.None);
        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForClaudeReport, CancellationToken.None);
        harness.ClaudeWatcher.RaiseStableChange("a report", "claude-hash-1");

        var status = await WaitForActionAsync(harness, "unchanged since it was last acted on");

        // Naming the file and the remedy is the point: "waiting" alone is what
        // made this indistinguishable from an agent still working.
        Assert.Contains(harness.Configuration.ClaudeReportFileName, status.LastAction!, StringComparison.Ordinal);
        Assert.Contains("Reset", status.LastAction!, StringComparison.Ordinal);

        // And it is still a wait, not an error: a new revision would be acted on.
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
    }

    [Fact]
    public async Task ThePromptCheckpointSaysTheSameThingAboutCodexsFile()
    {
        var harness = new OrchestratorTestHarness();
        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);

        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");
        await harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport, TimeSpan.FromSeconds(20));

        await harness.Orchestrator.StopAsync(CancellationToken.None);
        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);
        harness.CodexWatcher.RaiseStableChange("next task", "codex-hash-1");

        var status = await WaitForActionAsync(harness, "unchanged since it was last acted on");
        Assert.Contains(harness.Configuration.CodexPromptFileName, status.LastAction!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGenuinelyNewRevisionIsStillActedOn()
    {
        // The guard above must not swallow real work. Same file, different
        // content, after a restart: this is the ordinary case and it has to run.
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("a report", "claude-hash-1");
        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt, TimeSpan.FromSeconds(20));

        await harness.Orchestrator.StopAsync(CancellationToken.None);
        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForClaudeReport, CancellationToken.None);
        harness.ClaudeWatcher.RaiseStableChange("a second report", "claude-hash-2");

        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt, TimeSpan.FromSeconds(20));
    }

    private static async Task<BridgeStatusView> WaitForActionAsync(OrchestratorTestHarness harness, string fragment)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
            if (status.LastAction?.Contains(fragment, StringComparison.Ordinal) == true)
            {
                return status;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"No status action ever mentioned '{fragment}'.");
    }
}
