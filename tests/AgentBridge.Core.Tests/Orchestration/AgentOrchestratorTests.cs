using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Tests.TestDoubles;

namespace AgentBridge.Core.Tests.Orchestration;

public class AgentOrchestratorTests
{
    [Fact]
    public async Task Start_NonDryRunWithSimulationAdapters_FailsClosedBeforeStartingWatchers()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = false,
        };
        var harness = new OrchestratorTestHarness(config, simulationOnlyAdapters: true);

        await harness.StartAsync();

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Error, status.CurrentState);
        Assert.Contains("cannot verify real message delivery", status.LastError);
        Assert.False(harness.WatcherFactory.WasCreated(harness.ClaudeReportPath));
        Assert.False(harness.WatcherFactory.WasCreated(harness.CodexPromptPath));
        Assert.Equal(0, harness.ClaudeAdapter.State.SendMessageCallCount);
        Assert.Equal(0, harness.CodexAdapter.State.SendMessageCallCount);
    }

    [Fact]
    public async Task Start_DryRunWithSimulationAdapters_RemainsSupported()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = true,
        };
        var harness = new OrchestratorTestHarness(config, simulationOnlyAdapters: true);

        await harness.StartAsync();

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.True(harness.ClaudeWatcher.IsRunning);
        Assert.True(harness.CodexWatcher.IsRunning);
    }

    [Fact]
    public async Task Start_WithInvalidProjectPath_TransitionsToError()
    {
        var harness = new OrchestratorTestHarness();
        harness.ProjectService.IsValid = false;
        harness.ProjectService.Errors = ["bad path"];

        await harness.StartAsync();

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Error, status.CurrentState);
        Assert.Contains("bad path", status.LastError);
    }

    [Fact]
    public async Task Start_FreshState_TransitionsToWaitingForClaudeReport_AndStartsBothWatchers()
    {
        var harness = new OrchestratorTestHarness();

        await harness.StartAsync();

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Equal(0, status.CurrentIteration);
        Assert.True(harness.ClaudeWatcher.IsRunning);
        Assert.True(harness.CodexWatcher.IsRunning);
    }

    [Fact]
    public async Task StartAt_CodexCheckpoint_WaitsForNextCodexPromptWithoutSendingOrResettingFiles()
    {
        var harness = new OrchestratorTestHarness();

        await harness.Orchestrator.StartAtAsync(
            BridgeStartPoint.WaitForCodexPrompt,
            CancellationToken.None);

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForCodexPrompt, status.CurrentState);
        Assert.Equal(1, status.CurrentIteration);
        Assert.True(harness.ClaudeWatcher.IsRunning);
        Assert.True(harness.CodexWatcher.IsRunning);
        Assert.Empty(harness.ClaudeAdapter.State.SentMessages);
        Assert.Empty(harness.CodexAdapter.State.SentMessages);
    }

    [Fact]
    public async Task ClaudeReportChange_InvokesCodexAndAdvancesState()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("# report", "hash1");

        var status = await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(1, status.CurrentIteration);
        Assert.Single(harness.CodexAdapter.State.SentMessages);
        Assert.Contains("iteration 1 of at most 50", harness.CodexAdapter.State.SentMessages[0]);
        Assert.Empty(harness.ClaudeAdapter.State.SentMessages);
    }

    [Fact]
    public async Task FullCycle_ClaudeThenCodexThenClaudeAgain_LoopsBackAndIncrementsIteration()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");
        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);

        harness.CodexWatcher.RaiseStableChange("p1", "ph1");
        var back = await harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport);

        Assert.Single(harness.ClaudeAdapter.State.SentMessages);
        Assert.Equal(1, back.CurrentIteration);

        harness.ClaudeWatcher.RaiseStableChange("r2", "h2");
        var status = await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(2, status.CurrentIteration);
        Assert.Equal(2, harness.CodexAdapter.State.SentMessages.Count);
    }

    [Fact]
    public async Task DuplicateClaudeReportHash_NeverRetriggersProcessing()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");
        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);

        harness.CodexWatcher.RaiseStableChange("p1", "ph1");
        await harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport);

        harness.ClaudeWatcher.RaiseStableChange("r1", "h1"); // same hash as before
        await Task.Delay(200);

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Equal(1, status.CurrentIteration);
        Assert.Single(harness.CodexAdapter.State.SentMessages);
    }

    [Fact]
    public async Task CodexPromptChange_IgnoredWhileStillWaitingForClaudeReport()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        harness.CodexWatcher.RaiseStableChange("premature prompt", "phash1");
        await Task.Delay(200);

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Empty(harness.ClaudeAdapter.State.SentMessages);
    }

    [Fact]
    public async Task ClaudeReportChange_IsDeferredUntilClaudeStopsProcessing()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();
        harness.ClaudeAdapter.State.IsProcessing = true;
        harness.ClaudeWatcher.ArrangeCheckNowResult("final report", "final-hash");

        harness.ClaudeWatcher.RaiseStableChange("intermediate report", "intermediate-hash");
        await Task.Delay(150);

        var deferred = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, deferred.CurrentState);
        Assert.Empty(harness.CodexAdapter.State.SentMessages);

        harness.ClaudeAdapter.State.IsProcessing = false;
        var completed = await harness.WaitForStateAsync(
            BridgeState.WaitingForCodexPrompt,
            TimeSpan.FromSeconds(3));

        Assert.Equal(1, completed.CurrentIteration);
        Assert.Single(harness.CodexAdapter.State.SentMessages);
        Assert.Equal("final-hash", harness.StateStore.Current?.LastClaudeReportHash);
    }

    [Fact]
    public async Task StatusReportsBusyWhenClaudeExposesActiveProcessing()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.IsProcessing = true;
        harness.ClaudeAdapter.State.Status = AgentStatus.Ready;

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AgentStatus.Busy, status.ClaudeStatus);
    }

    [Fact]
    public async Task ClaudeReportChange_WaitsThroughQuotaResetAndAutomaticResume()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();
        harness.ClaudeAdapter.State.Status = AgentStatus.RateLimited;
        harness.ClaudeWatcher.ArrangeCheckNowResult("completed after reset", "post-reset-hash");

        harness.ClaudeWatcher.RaiseStableChange("partial report", "partial-hash");
        await Task.Delay(150);

        var limited = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, limited.CurrentState);
        Assert.Equal(AgentStatus.RateLimited, limited.ClaudeStatus);
        Assert.Contains("automatic reset", limited.LastAction);
        Assert.Empty(harness.CodexAdapter.State.SentMessages);

        harness.ClaudeAdapter.State.Status = AgentStatus.Ready;
        harness.ClaudeAdapter.State.IsProcessing = true;
        await Task.Delay(1100);
        Assert.Empty(harness.CodexAdapter.State.SentMessages);

        harness.ClaudeAdapter.State.IsProcessing = false;
        var completed = await harness.WaitForStateAsync(
            BridgeState.WaitingForCodexPrompt,
            TimeSpan.FromSeconds(3));

        Assert.Equal(1, completed.CurrentIteration);
        Assert.Single(harness.CodexAdapter.State.SentMessages);
        Assert.Equal("post-reset-hash", harness.StateStore.Current?.LastClaudeReportHash);
    }

    [Fact]
    public async Task StartAtClaudeCheckpoint_ReportsExistingQuotaPauseBeforeAnyFileChange()
    {
        var harness = new OrchestratorTestHarness();
        harness.ClaudeAdapter.State.Status = AgentStatus.RateLimited;

        await harness.Orchestrator.StartAtAsync(
            BridgeStartPoint.WaitForClaudeReport,
            CancellationToken.None);

        var limited = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, limited.CurrentState);
        Assert.Equal(AgentStatus.RateLimited, limited.ClaudeStatus);
        Assert.Contains("automatic reset", limited.LastAction);
        Assert.Empty(harness.CodexAdapter.State.SentMessages);

        harness.ClaudeAdapter.State.Status = AgentStatus.Ready;
    }

    [Fact]
    public async Task ConcurrentIdenticalFileEvents_OnlyProcessOnce()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        for (var i = 0; i < 5; i++)
        {
            harness.ClaudeWatcher.RaiseStableChange("r1", "h1");
        }

        var status = await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(1, status.CurrentIteration);
        Assert.Single(harness.CodexAdapter.State.SentMessages);
    }

    [Fact]
    public async Task MaximumIterations_StopsAutomationInsteadOfExceedingLimit()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake",
            DryRun = false,
            MaximumIterations = 1,
            AgentTimeoutSeconds = 2,
            RetryCount = 0,
        };
        var harness = new OrchestratorTestHarness(config);
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");
        await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        harness.CodexWatcher.RaiseStableChange("p1", "ph1");
        await harness.WaitForStateAsync(BridgeState.WaitingForClaudeReport);

        harness.ClaudeWatcher.RaiseStableChange("r2", "h2"); // would be iteration 2, exceeds the limit of 1
        var status = await harness.WaitForStateAsync(BridgeState.Stopped);

        Assert.Equal(1, status.CurrentIteration);
        Assert.Contains("Maximum iterations", status.LastAction);
        Assert.False(harness.ClaudeWatcher.IsRunning);
        Assert.False(harness.CodexWatcher.IsRunning);

        await harness.Orchestrator.StopAsync(CancellationToken.None);
        Assert.False(harness.ClaudeWatcher.IsRunning);
        Assert.False(harness.CodexWatcher.IsRunning);

        await harness.StartAsync();
        Assert.Equal(
            BridgeState.WaitingForClaudeReport,
            (await harness.Orchestrator.GetStatusAsync(CancellationToken.None)).CurrentState);
    }

    [Fact]
    public async Task Pause_BlocksProcessing_ResumePicksUpMissedChangeViaCheckNow()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        await harness.Orchestrator.PauseAsync(CancellationToken.None);
        var paused = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Paused, paused.CurrentState);
        Assert.True(paused.IsPaused);

        // While paused, a live watcher would still be running but the orchestrator ignores
        // its events (state guard). Production picks up anything missed via CheckNowAsync on
        // Resume — arrange what that call will "discover".
        harness.ClaudeWatcher.ArrangeCheckNowResult("r1", "h1");
        harness.ClaudeWatcher.RaiseStableChange("should be ignored", "ignored-hash");
        await Task.Delay(100);
        Assert.Equal(BridgeState.Paused, (await harness.Orchestrator.GetStatusAsync(CancellationToken.None)).CurrentState);

        await harness.Orchestrator.ResumeAsync(CancellationToken.None);

        var status = await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(1, status.CurrentIteration);
    }

    [Fact]
    public async Task Stop_StopsWatchersAndTransitionsToStopped()
    {
        var harness = new OrchestratorTestHarness();
        await harness.StartAsync();

        await harness.Orchestrator.StopAsync(CancellationToken.None);

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Stopped, status.CurrentState);
        Assert.False(harness.ClaudeWatcher.IsRunning);
        Assert.False(harness.CodexWatcher.IsRunning);
    }

    [Fact]
    public async Task Stop_CancelsAnInFlightAgentInvocationPromptly()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = false,
            AgentTimeoutSeconds = 30,
            RetryCount = 0,
        };
        var harness = new OrchestratorTestHarness(config);
        harness.CodexAdapter.State.SendMessageDelay = TimeSpan.FromSeconds(20);
        await harness.StartAsync();
        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");
        await harness.WaitForStateAsync(BridgeState.CodexProcessing);

        var stopTask = harness.Orchestrator.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Stopped, status.CurrentState);
        Assert.False(harness.ClaudeWatcher.IsRunning);
        Assert.False(harness.CodexWatcher.IsRunning);
        Assert.Empty(harness.CodexAdapter.State.SentMessages);
    }

    [Fact]
    public async Task CodexSendFailure_TransitionsToError_WithDescriptiveLastError()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = false,
            RetryCount = 3,
            RetryInitialDelayMilliseconds = 1,
            RetryMaxDelayMilliseconds = 2,
        };
        var harness = new OrchestratorTestHarness(config);
        harness.CodexAdapter.State.SendMessageSucceeds = false;
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");

        var status = await harness.WaitForStateAsync(BridgeState.Error);
        Assert.NotNull(status.LastError);
        Assert.Contains("Codex", status.LastError);
        Assert.Equal(1, harness.CodexAdapter.State.SendMessageCallCount);
    }

    [Fact]
    public async Task RetryCodexDelivery_AfterFailure_ResendsWithoutAdvancingIteration()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = false,
            RetryCount = 0,
        };
        var harness = new OrchestratorTestHarness(config);
        harness.CodexAdapter.State.SendMessageSucceeds = false;
        await harness.StartAsync();
        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");
        await harness.WaitForStateAsync(BridgeState.Error);

        harness.CodexAdapter.State.SendMessageSucceeds = true;
        await harness.Orchestrator.RetryCodexDeliveryAsync(CancellationToken.None);

        var status = await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(1, status.CurrentIteration);
        Assert.Equal(2, harness.CodexAdapter.State.SendMessageCallCount);
        Assert.Single(harness.CodexAdapter.State.SentMessages);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task RetryCodexDelivery_AfterRestartedError_ReactivatesBothWatchers()
    {
        var harness = new OrchestratorTestHarness();
        harness.StateStore.SeedLoaded(new BridgeStateSnapshot
        {
            CurrentState = BridgeState.Error,
            CurrentIteration = 2,
            MaximumIterations = 50,
            LastClaudeReportHash = "report-hash",
            LastError = "Failed to deliver instruction to Codex for iteration 2.",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await harness.StartAsync();

        await harness.Orchestrator.RetryCodexDeliveryAsync(CancellationToken.None);

        var status = await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(2, status.CurrentIteration);
        Assert.True(harness.ClaudeWatcher.IsRunning);
        Assert.True(harness.CodexWatcher.IsRunning);
        Assert.Single(harness.CodexAdapter.State.SentMessages);
    }

    [Fact]
    public async Task RetryCodexDelivery_RefusesWhileClaudeIsStillProcessing()
    {
        var harness = new OrchestratorTestHarness();
        harness.StateStore.SeedLoaded(new BridgeStateSnapshot
        {
            CurrentState = BridgeState.WaitingForClaudeReport,
            CurrentIteration = 2,
            MaximumIterations = 50,
            LastClaudeReportHash = "report-hash",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await harness.StartAsync();
        harness.ClaudeAdapter.State.IsProcessing = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Orchestrator.RetryCodexDeliveryAsync(CancellationToken.None));

        Assert.Contains("still processing", error.Message);
        Assert.Empty(harness.CodexAdapter.State.SentMessages);
        Assert.Equal(
            BridgeState.WaitingForClaudeReport,
            (await harness.Orchestrator.GetStatusAsync(CancellationToken.None)).CurrentState);
    }

    [Fact]
    public async Task ReadinessFalse_IsRetriedWithinBudget_AndMessageIsNeverSent()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = false,
            RetryCount = 2,
            RetryInitialDelayMilliseconds = 1,
            RetryMaxDelayMilliseconds = 2,
        };
        var harness = new OrchestratorTestHarness(config);
        harness.CodexAdapter.State.IsReady = false;
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");

        var status = await harness.WaitForStateAsync(BridgeState.Error);
        Assert.Contains("Codex", status.LastError);
        Assert.Equal(3, harness.CodexAdapter.State.IsReadyCallCount);
        Assert.Equal(0, harness.CodexAdapter.State.FindConversationCallCount);
        Assert.Equal(0, harness.CodexAdapter.State.SendMessageCallCount);
    }

    [Fact]
    public async Task AgentTimeout_TransitionsToError_WithoutHangingForever()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake",
            DryRun = false,
            MaximumIterations = 50,
            AgentTimeoutSeconds = 1,
            RetryCount = 0,
        };
        var harness = new OrchestratorTestHarness(config);
        harness.CodexAdapter.State.SendMessageDelay = TimeSpan.FromSeconds(3);
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");

        var status = await harness.WaitForStateAsync(BridgeState.Error, TimeSpan.FromSeconds(5));
        Assert.Contains("Codex", status.LastError);
    }

    [Fact]
    public async Task CorruptedPersistedState_TransitionsToError_AndResetRecoversToFreshStart()
    {
        var harness = new OrchestratorTestHarness();
        harness.StateStore.SeedCorrupted();

        await harness.StartAsync();
        Assert.Equal(BridgeState.Error, (await harness.Orchestrator.GetStatusAsync(CancellationToken.None)).CurrentState);

        await harness.Orchestrator.ResetStateAsync(CancellationToken.None);
        var afterReset = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Idle, afterReset.CurrentState);
        Assert.Equal(0, afterReset.CurrentIteration);

        await harness.StartAsync();
        Assert.Equal(BridgeState.WaitingForClaudeReport, (await harness.Orchestrator.GetStatusAsync(CancellationToken.None)).CurrentState);
    }

    [Fact]
    public async Task AmbiguousMidActionStateOnRestart_NeverBlindlyResumes()
    {
        var harness = new OrchestratorTestHarness();
        harness.StateStore.SeedLoaded(new BridgeStateSnapshot
        {
            CurrentState = BridgeState.CodexProcessing, // an action may have been in-flight when the app died
            CurrentIteration = 3,
            MaximumIterations = 50,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await harness.StartAsync();

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Error, status.CurrentState);
        // Never even created watchers on an unsafe recovery — Start bailed out before EnsureWatchers.
        Assert.False(harness.WatcherFactory.WasCreated(harness.ClaudeReportPath));
    }

    [Theory]
    [InlineData(BridgeState.WaitingForCodex)]
    [InlineData(BridgeState.WaitingForClaude)]
    public async Task PendingPreActionStateOnRestart_FailsClosedWithoutInvokingAgent(BridgeState persistedState)
    {
        var harness = new OrchestratorTestHarness();
        harness.StateStore.SeedLoaded(new BridgeStateSnapshot
        {
            CurrentState = persistedState,
            CurrentIteration = 3,
            MaximumIterations = 50,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await harness.StartAsync();

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.Error, status.CurrentState);
        Assert.Contains("Ambiguous state", status.LastError);
        Assert.Equal(0, harness.ClaudeAdapter.State.SendMessageCallCount);
        Assert.Equal(0, harness.CodexAdapter.State.SendMessageCallCount);
        Assert.False(harness.WatcherFactory.WasCreated(harness.ClaudeReportPath));
        Assert.False(harness.WatcherFactory.WasCreated(harness.CodexPromptPath));
    }

    [Fact]
    public async Task DryRun_AdvancesStateButNeverActuallyCallsSendMessage()
    {
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake",
            DryRun = true,
            MaximumIterations = 50,
            AgentTimeoutSeconds = 2,
            RetryCount = 0,
        };
        var harness = new OrchestratorTestHarness(config);
        await harness.StartAsync();

        harness.ClaudeWatcher.RaiseStableChange("r1", "h1");

        var status = await harness.WaitForStateAsync(BridgeState.WaitingForCodexPrompt);
        Assert.Equal(1, status.CurrentIteration);
        Assert.Empty(harness.CodexAdapter.State.SentMessages);
        Assert.Contains("Dry Run", status.LastAction);
    }

    [Fact]
    public async Task SuccessfulRestart_ClearsAStalePreviousError()
    {
        var harness = new OrchestratorTestHarness();
        harness.ProjectService.IsValid = false;
        await harness.StartAsync();
        Assert.NotNull((await harness.Orchestrator.GetStatusAsync(CancellationToken.None)).LastError);

        await harness.Orchestrator.ResetStateAsync(CancellationToken.None);
        harness.ProjectService.IsValid = true;
        await harness.StartAsync();

        var status = await harness.Orchestrator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(BridgeState.WaitingForClaudeReport, status.CurrentState);
        Assert.Null(status.LastError);
    }
}
