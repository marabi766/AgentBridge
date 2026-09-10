using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;
using Xunit;

namespace AgentBridge.Core.Tests.Orchestration;

/// <summary>
/// What happened on a real run: Claude's iteration-1 attempt timed out and was
/// killed without updating its report. The operator restarted at the "Claude is
/// working" checkpoint meaning to press Continue — but the report still on disk
/// from an earlier attempt, older than the current Codex prompt, was consumed as
/// though it answered that prompt, and the run advanced past the point where
/// Continue was possible.
///
/// The protocol strictly alternates, so a genuine reply is always newer than
/// what it replies to. This check reads the two files' times against each other,
/// which — unlike the hash and instruction-time guards — still works after a
/// restart, when nothing in memory says an instruction is outstanding.
/// </summary>
public sealed class StaleProtocolFileTests
{
    [Fact]
    public async Task AReportOlderThanTheCurrentPromptIsNotTreatedAsAReplyToIt()
    {
        var harness = new OrchestratorTestHarness();

        var promptWrittenUtc = DateTimeOffset.UtcNow;
        harness.ProjectService.WriteTimesUtc[harness.CodexPromptPath] = promptWrittenUtc;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForClaudeReport, CancellationToken.None);

        // The report on disk is from a previous attempt, half an hour before the
        // prompt it is supposedly answering.
        harness.ClaudeWatcher.RaiseStableChange(
            "an old report", "claude-stale", promptWrittenUtc.AddMinutes(-30));

        var status = await WaitForActionAsync(harness, "leftover from an earlier cycle");

        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Equal(0, harness.CodexAdapter.State.SendMessageCallCount);
        // A genuinely new report — written after the prompt — is still acted on.
        harness.ClaudeWatcher.RaiseStableChange(
            "the real report", "claude-fresh", promptWrittenUtc.AddMinutes(5));
        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task APromptOlderThanTheCurrentReportIsNotTreatedAsAReplyToIt()
    {
        var harness = new OrchestratorTestHarness();

        var reportWrittenUtc = DateTimeOffset.UtcNow;
        harness.ProjectService.WriteTimesUtc[harness.ClaudeReportPath] = reportWrittenUtc;

        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForCodexPrompt, CancellationToken.None);

        harness.CodexWatcher.RaiseStableChange(
            "an old prompt", "codex-stale", reportWrittenUtc.AddMinutes(-30));

        var status = await WaitForActionAsync(harness, "leftover from an earlier cycle");
        Assert.Equal(BridgeState.WaitingForCodexPrompt, status.CurrentState);
        Assert.Equal(0, harness.ClaudeAdapter.State.SendMessageCallCount);
    }

    [Fact]
    public async Task WithNoCounterpartFileYetTheChangeIsActedOnNormally()
    {
        // A first cycle: the operator's hand-written prompt exists, no report has
        // ever been written. Nothing for the report to be "older than".
        var harness = new OrchestratorTestHarness();
        await harness.Orchestrator.StartAtAsync(BridgeStartPoint.WaitForClaudeReport, CancellationToken.None);

        harness.ClaudeWatcher.RaiseStableChange("first report", "claude-1", DateTimeOffset.UtcNow);

        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt, TimeSpan.FromSeconds(20));
        Assert.Equal(1, harness.CodexAdapter.State.SendMessageCallCount);
    }

    private static async Task<BridgeStatusView> WaitForActionAsync(OrchestratorTestHarness harness, string fragment)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        BridgeStatusView status;
        do
        {
            status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
            if (status.LastAction?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true)
            {
                return status;
            }

            await Task.Delay(20);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException(
            $"Expected LastAction to contain \"{fragment}\" but it was \"{status.LastAction}\".");
    }
}
