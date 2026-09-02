using AgentBridge.Abstractions.Models;
using AgentBridge.Integration.Tests.TestSupport;

namespace AgentBridge.Integration.Tests;

/// <summary>
/// End-to-end simulation of the real workflow: real file watcher, real state
/// persistence, real git introspection, fake Claude/Codex adapters, driven by
/// actual writes to ClaudeResultReport.md / CodexPrompt.md on disk.
/// </summary>
public sealed class FullOrchestrationCycleTests : IDisposable
{
    private readonly IntegrationTestHarness _harness = new();

    [Fact]
    public async Task ThreeCompleteIterations_AdvanceCorrectly_WithCorrectMessageContent()
    {
        await _harness.StartAsync();

        for (var i = 1; i <= 3; i++)
        {
            await _harness.WriteClaudeReportAsync($"# Claude Result Report\nIteration {i} complete.");
            var afterClaude = await _harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
            Assert.Equal(i, afterClaude.CurrentIteration);
            Assert.Contains($"iteration {i} of at most 50", _harness.CodexAdapter.State.SentMessages[^1]);

            await _harness.WriteCodexPromptAsync($"# Codex Prompt\nNext step for iteration {i}.");
            var afterCodex = await _harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport);
            Assert.Equal(i, afterCodex.CurrentIteration);
        }

        Assert.Equal(3, _harness.ClaudeAdapter.State.SentMessages.Count);
        Assert.Equal(3, _harness.CodexAdapter.State.SentMessages.Count);
    }

    [Fact]
    public async Task RapidDuplicateSaves_NeverProduceMoreThanOneAgentInvocation()
    {
        await _harness.StartAsync();

        // Simulate an editor autosaving the same final content several times in a burst.
        for (var i = 0; i < 5; i++)
        {
            await _harness.WriteClaudeReportAsync("# Report\nfinal content");
            await Task.Delay(15); // well inside the configured 80ms debounce window
        }

        var status = await _harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(1, status.CurrentIteration);
        Assert.Single(_harness.CodexAdapter.State.SentMessages);
    }

    [Fact]
    public async Task MaximumIterations_StopsAutomationAfterTheConfiguredLimit()
    {
        using var harness = new IntegrationTestHarness(maximumIterations: 2);
        await harness.StartAsync();

        for (var i = 1; i <= 2; i++)
        {
            await harness.WriteClaudeReportAsync($"report {i}");
            await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
            await harness.WriteCodexPromptAsync($"prompt {i}");
            await harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport);
        }

        await harness.WriteClaudeReportAsync("report 3 — should be refused");
        var status = await harness.WaitForStateAsync(BridgeState.Stopped);

        Assert.Equal(2, status.CurrentIteration);
        Assert.Equal(2, harness.CodexAdapter.State.SentMessages.Count);
    }

    [Fact]
    public async Task PauseAndResume_AcrossRealFileWrites()
    {
        await _harness.StartAsync();

        await _harness.WriteClaudeReportAsync("report 1");
        await _harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        await _harness.WriteCodexPromptAsync("prompt 1");
        await _harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport);

        await _harness.Orchestrator.PauseAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Paused, (await _harness.Orchestrator.GetStatusAsync(CancellationToken.None)).CurrentState);

        // Written while paused — must NOT be processed yet.
        await _harness.WriteClaudeReportAsync("report 2 while paused");
        await Task.Delay(400);
        Assert.Equal(BridgeState.Paused, (await _harness.Orchestrator.GetStatusAsync(CancellationToken.None)).CurrentState);
        Assert.Single(_harness.CodexAdapter.State.SentMessages);

        await _harness.Orchestrator.ResumeAsync(CancellationToken.None);

        var status = await _harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(2, status.CurrentIteration);
        Assert.Equal(2, _harness.CodexAdapter.State.SentMessages.Count);
    }

    [Fact]
    public async Task PersistedState_SurvivesAppRestart_AfterACleanStop()
    {
        await _harness.StartAsync();
        await _harness.WriteClaudeReportAsync("report 1");
        await _harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        await _harness.WriteCodexPromptAsync("prompt 1");
        await _harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport);

        await _harness.Orchestrator.StopAsync(CancellationToken.None);

        using var restarted = _harness.RebuildOrchestratorSimulatingRestart();
        await restarted.StartAsync(CancellationToken.None);

        var status = await restarted.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Equal(1, status.CurrentIteration); // iteration count carried over from the persisted snapshot
    }

    [Fact]
    public async Task PersistedAmbiguousMidActionState_NeverAutoResumes_EvenAfterRestart()
    {
        // Simulate a crash: something wrote CodexProcessing to disk but the process died
        // before completing — this must never be blindly resumed.
        await _harness.SeedPersistedStateAsync(new BridgeStateSnapshot
        {
            CurrentState = BridgeState.CodexProcessing,
            CurrentIteration = 5,
            MaximumIterations = 50,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await _harness.StartAsync();

        var status = await _harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Error, status.CurrentState);
        Assert.Contains("Ambiguous state", status.LastError);

        await _harness.Orchestrator.ResetStateAsync(CancellationToken.None);
        await _harness.StartAsync();
        var recovered = await _harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, recovered.CurrentState);
        Assert.Equal(0, recovered.CurrentIteration);
    }

    [Fact]
    public async Task GitStatus_ReflectsActualRepositoryStateThroughoutTheCycle()
    {
        await _harness.StartAsync();
        var before = await _harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal("main", before.GitBranch);
        Assert.Equal("Clean", before.GitWorkingTreeSummary);

        await _harness.WriteClaudeReportAsync("report 1");
        var after = await _harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.NotEqual("Clean", after.GitWorkingTreeSummary); // the new report file shows up as a modification
    }

    [Fact]
    public async Task AgentUnreachable_TransitionsToErrorRatherThanHangingOrCrashing()
    {
        _harness.CodexAdapter.State.IsApplicationRunning = false;
        await _harness.StartAsync();

        await _harness.WriteClaudeReportAsync("report 1");

        var status = await _harness.WaitForStateAsync(BridgeState.Error);
        Assert.NotNull(status.LastError);
    }

    public void Dispose() => _harness.Dispose();
}
