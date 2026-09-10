using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Core.StateMachine;
using AgentBridge.Core.Templates;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Core.Orchestration;

/// <summary>
/// Central orchestration engine. Coordinates file watchers, the state machine,
/// agent adapters, persistence, retry/timeout policy and notifications. Contains
/// no UI-technology dependencies whatsoever — it is fully exercised by
/// AgentBridge.Integration.Tests using fake adapters and a real file watcher.
///
/// Concurrency model: a single async-friendly semaphore (<see cref="_actionLock"/>)
/// serializes every state-changing operation (file-change handlers, Pause, Stop,
/// ResetState). Only one Claude or Codex invocation is ever in flight at a time.
/// </summary>
public sealed class AgentOrchestrator : IOrchestratorService, IDisposable
{
    private readonly IStateStore _stateStore;
    private readonly IConfigurationService _configService;
    private readonly IFileWatcherFactory _watcherFactory;
    private readonly IAgentAdapterProvider _agentAdapterProvider;
    private readonly IGitService _gitService;
    private readonly ITemplateEngine _templateEngine;
    private readonly IRetryPolicy _retryPolicy;
    private readonly INotificationService _notificationService;
    private readonly IProjectService _projectService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentOrchestrator> _logger;

    // Focus, editor readback and send receipt each have their own deadline; one
    // delivery has to be allowed to outlast their sum, or it can never finish.
    private static readonly TimeSpan TypicalDeliveryTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// What Continue sends. One word on purpose: the session it lands in already
    /// holds the instruction, the repository and everything the agent had done,
    /// so restating any of that would only argue with what it already knows.
    /// </summary>
    private const string ContinueInstruction = "continue";

    // After an agent stops and its protocol file has been rechecked, how long a
    // file that is genuinely on its way is given to arrive before the iteration
    // is called empty.
    private static readonly TimeSpan FileArrivalGrace = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly BridgeStateMachine _stateMachine = new();

    private BridgeConfiguration _configuration = BridgeConfiguration.CreateDefault();
    private IFileWatcher? _claudeWatcher;
    private IFileWatcher? _codexWatcher;
    private CancellationTokenSource? _runCts;

    private int _currentIteration;
    private string? _lastClaudeReportHash;
    private string? _lastCodexPromptHash;
    private DateTimeOffset? _lastClaudeReportUpdateUtc;
    private DateTimeOffset? _lastCodexPromptUpdateUtc;
    // When this run last handed an instruction to each agent. A protocol file
    // written before that moment cannot be the answer to it. Deliberately not
    // persisted: after a restart no instruction is outstanding, and the hash
    // comparison alone must stay free to consume a file that was written while
    // the bridge was down.
    private DateTimeOffset? _claudeInstructionSentAtUtc;
    private DateTimeOffset? _codexInstructionSentAtUtc;
    private DateTimeOffset? _startedAtUtc;
    private AgentRole? _lastAgent;
    private string? _lastAction;
    private string? _lastError;
    private GitRepositoryStatus? _lastGitStatus;
    private AgentStatus _lastClaudeAgentStatus = AgentStatus.Unknown;
    private AgentStatus _lastCodexAgentStatus = AgentStatus.Unknown;
    private int _claudeCompletionProbeActive;
    private int _codexCompletionProbeActive;
    // Bumped whenever the operator hands a delivery to an agent by hand — Continue
    // or Retry. The completion probe captures this on entry and, when it changes,
    // re-observes the new delivery from the top instead of resending the
    // iteration's instruction on top of it. Without this a manual Continue during
    // an allowance wait would be followed, minutes later, by an automatic full
    // resend from the probe that was still counting down to the old reset.
    private long _claudeDeliveryEpoch;
    private long _codexDeliveryEpoch;
    // How many times in a row an exhausted allowance has been waited out and the
    // instruction resent, per role. Reset once the agent delivers its file, so
    // the count only ever describes one stuck iteration.
    private int _claudeQuotaResends;
    private int _codexQuotaResends;
    private int _claudeFailedRunResends;
    private int _codexFailedRunResends;

    public AgentOrchestrator(
        IStateStore stateStore,
        IConfigurationService configService,
        IFileWatcherFactory watcherFactory,
        IAgentAdapterProvider agentAdapterProvider,
        IGitService gitService,
        ITemplateEngine templateEngine,
        IRetryPolicy retryPolicy,
        INotificationService notificationService,
        IProjectService projectService,
        TimeProvider timeProvider,
        ILogger<AgentOrchestrator> logger)
    {
        _stateStore = stateStore;
        _configService = configService;
        _watcherFactory = watcherFactory;
        _agentAdapterProvider = agentAdapterProvider;
        _gitService = gitService;
        _templateEngine = templateEngine;
        _retryPolicy = retryPolicy;
        _notificationService = notificationService;
        _projectService = projectService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event EventHandler<BridgeStatusView>? StatusChanged;

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken) =>
        StartCoreAsync(null, cancellationToken);

    public Task StartAtAsync(BridgeStartPoint startPoint, CancellationToken cancellationToken) =>
        StartCoreAsync(startPoint, cancellationToken);

    private async Task StartCoreAsync(BridgeStartPoint? requestedStartPoint, CancellationToken cancellationToken)
    {
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stateMachine.Current is not (BridgeState.Idle or BridgeState.Stopped or BridgeState.Error))
            {
                _logger.LogInformation("Start requested but bridge is already {State}; ignoring.", _stateMachine.Current);
                return;
            }

            // No instruction from a previous run is outstanding across a start, so
            // a protocol file written while the bridge was stopped must stay
            // eligible on its hash alone.
            _claudeInstructionSentAtUtc = null;
            _codexInstructionSentAtUtc = null;

            _configuration = await _configService.LoadAsync(cancellationToken).ConfigureAwait(false);

            var validation = await _projectService.ValidateProjectAsync(_configuration, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                SetError($"Project path invalid: {string.Join("; ", validation.Errors)}");
                _stateMachine.ForceState(BridgeState.Error, null);
                PublishStatus();
                return;
            }

            if (!_configuration.DryRun)
            {
                var unsupportedAdapters = new[]
                {
                    _agentAdapterProvider.GetAdapter(AgentRole.Claude),
                    _agentAdapterProvider.GetAdapter(AgentRole.Codex),
                }
                .Where(adapter => !adapter.SupportsRealMessageDelivery)
                .Select(adapter => adapter.Name)
                .ToArray();

                if (unsupportedAdapters.Length > 0)
                {
                    SetError(
                        "A real run was requested, but these adapters cannot verify real message delivery: " +
                        string.Join(", ", unsupportedAdapters) + ". Enable Dry Run or configure delivery-capable adapters.");
                    _stateMachine.ForceState(BridgeState.Error, null);
                    PublishStatus();
                    return;
                }
            }

            var loadResult = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            switch (loadResult.Status)
            {
                case StateLoadStatus.Corrupted:
                    SetError($"Persisted state is corrupted and cannot be safely resumed: {loadResult.ErrorMessage}. Call ResetState to start fresh.");
                    _stateMachine.ForceState(BridgeState.Error, null);
                    PublishStatus();
                    await NotifyAsync("Agent Bridge — recovery required", _lastError!, NotificationLevel.Error, cancellationToken).ConfigureAwait(false);
                    return;
                case StateLoadStatus.Loaded:
                    ApplyRecoveredState(loadResult.Snapshot!);
                    break;
                case StateLoadStatus.NotFound:
                    InitializeFreshState();
                    break;
            }

            if (_stateMachine.Current == BridgeState.Error && requestedStartPoint is null)
            {
                _logger.LogWarning("Start aborted: bridge recovered into Error state ({Error}). Call ResetState first.", _lastError);
                PublishStatus();
                return;
            }

            if (requestedStartPoint is not null)
            {
                var selectedState = requestedStartPoint == BridgeStartPoint.WaitForCodexPrompt
                    ? BridgeState.WaitingForCodexPrompt
                    : BridgeState.WaitingForClaudeReport;
                _currentIteration = selectedState == BridgeState.WaitingForCodexPrompt
                    ? Math.Max(1, _currentIteration)
                    : _currentIteration;
                _lastError = null;
                _lastAction = selectedState == BridgeState.WaitingForCodexPrompt
                    ? "Started at operator-selected Codex checkpoint"
                    : "Started at operator-selected Claude checkpoint";
                _stateMachine.ForceState(selectedState, null);
            }

            // A successful Start means whatever error stopped a prior run is no longer
            // current — surfacing it forever in status would misrepresent the live state.
            _lastError = null;

            EnsureWatchers();
            _runCts = new CancellationTokenSource();

            if (_stateMachine.Current is BridgeState.Idle or BridgeState.Stopped)
            {
                Transition(BridgeState.WaitingForClaudeReport, "Bridge started");
            }

            _startedAtUtc ??= _timeProvider.GetUtcNow();
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);

            _claudeWatcher!.Start();
            _codexWatcher!.Start();

            _logger.LogInformation(
                "Agent Bridge started. State={State} Iteration={Iteration}/{Max} DryRun={DryRun}",
                _stateMachine.Current, _currentIteration, _configuration.MaximumIterations, _configuration.DryRun);
        }
        finally
        {
            _actionLock.Release();
        }

        // Pick up changes that happened while the bridge was not running. Outside the
        // lock: CheckNowAsync may synchronously raise StableChangeDetected, whose
        // handler re-enters _actionLock.
        if (_claudeWatcher is not null) await _claudeWatcher.CheckNowAsync(cancellationToken).ConfigureAwait(false);
        if (_codexWatcher is not null) await _codexWatcher.CheckNowAsync(cancellationToken).ConfigureAwait(false);

        // A quota pause may exist before any protocol-file change is observed.
        // Probe the expected agent at startup so the UI reports the real state and
        // the eventual automatic resume is followed without operator intervention.
        if (_stateMachine.Current == BridgeState.WaitingForClaudeReport && _claudeWatcher is not null)
        {
            await StartCompletionProbeIfAgentActiveAsync(
                AgentRole.Claude, _claudeWatcher, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RetryClaudeDeliveryAsync(CancellationToken cancellationToken)
    {
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stateMachine.Current != BridgeState.WaitingForClaudeReport)
            {
                throw new InvalidOperationException("Claude delivery can only be retried while waiting for its report.");
            }

            if (_currentIteration < 1 || string.IsNullOrWhiteSpace(_lastCodexPromptHash))
            {
                throw new InvalidOperationException("There is no verified Codex prompt available to resend.");
            }

            TakeOverDelivery(AgentRole.Claude);
            Transition(BridgeState.WaitingForClaude, $"Retrying Claude delivery for iteration {_currentIteration}");
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await InvokeClaudeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public async Task RetryCodexDeliveryAsync(CancellationToken cancellationToken)
    {
        IFileWatcher? claudeWatcher = null;
        IFileWatcher? codexWatcher = null;
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retryableError = _stateMachine.Current == BridgeState.Error
                && _lastError?.StartsWith("Failed to deliver instruction to Codex", StringComparison.Ordinal) == true;
            if (_stateMachine.Current is not (BridgeState.WaitingForClaudeReport or BridgeState.WaitingForCodexPrompt)
                && !retryableError)
            {
                throw new InvalidOperationException("Codex delivery can only be retried while waiting for its prompt or after a delivery failure.");
            }

            if (_currentIteration < 1 || string.IsNullOrWhiteSpace(_lastClaudeReportHash))
            {
                throw new InvalidOperationException("There is no verified Claude report available to resend.");
            }

            if (await _agentAdapterProvider.GetAdapter(AgentRole.Claude)
                    .IsProcessingAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Claude is still processing; wait for it to finish before sending its report to Codex.");
            }

            if (_runCts is null)
            {
                EnsureWatchers();
                _runCts = new CancellationTokenSource();
                _claudeWatcher!.Start();
                _codexWatcher!.Start();
            }
            claudeWatcher = _claudeWatcher;
            codexWatcher = _codexWatcher;

            TakeOverDelivery(AgentRole.Codex);
            _lastError = null;
            Transition(BridgeState.WaitingForCodex, $"Retrying Codex delivery for iteration {_currentIteration}");
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await InvokeCodexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actionLock.Release();
        }

        if (claudeWatcher is not null) await claudeWatcher.CheckNowAsync(cancellationToken).ConfigureAwait(false);
        if (codexWatcher is not null) await codexWatcher.CheckNowAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ContinueClaudeAsync(CancellationToken cancellationToken) =>
        ContinueAgentAsync(AgentRole.Claude, cancellationToken);

    public Task ContinueCodexAsync(CancellationToken cancellationToken) =>
        ContinueAgentAsync(AgentRole.Codex, cancellationToken);

    /// <summary>
    /// Opens a remote control session on Claude.
    ///
    /// Deliberately outside <c>_actionLock</c> and outside the state machine.
    /// This changes nothing about the cycle — it opens a copy of the
    /// conversation next to it — and taking the lock would mean an operator
    /// could not reach for their phone while the bridge was mid-delivery, which
    /// is exactly when they would want to.
    /// </summary>
    public async Task<RemoteControlSession?> OpenClaudeRemoteControlAsync(CancellationToken cancellationToken)
    {
        var adapter = _agentAdapterProvider.GetAdapter(AgentRole.Claude);
        if (adapter is not IOpensARemoteControlSession opener)
        {
            throw new InvalidOperationException(
                $"{adapter.Name} cannot open a remote control session.");
        }

        _lastAction = "Opening a remote control session on Claude…";
        PublishStatus();

        var session = await opener.OpenRemoteControlSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            _lastAction = "Claude did not return a remote control link. See the log for what it printed.";
            PublishStatus();
            return null;
        }

        _lastAction = $"Remote control session {session.SessionId} is open.";
        PublishStatus();

        // Worth sending: the link is for another device, and the notification
        // channels are how this run already reaches one.
        await NotifyAsync(
            "Agent Bridge — remote control",
            $"Claude session {session.SessionId} is ready to drive from another device:\n{session.Url}",
            NotificationLevel.Info,
            cancellationToken).ConfigureAwait(false);

        return session;
    }

    private long CurrentDeliveryEpoch(AgentRole role) =>
        Interlocked.Read(ref role == AgentRole.Claude ? ref _claudeDeliveryEpoch : ref _codexDeliveryEpoch);

    /// <summary>
    /// Records that the operator is taking a delivery over by hand. Any probe
    /// still watching the previous attempt will see the epoch move and step
    /// aside; an announced allowance wait is dropped, because the operator
    /// arranging capacity the bridge cannot see is the whole reason to override
    /// it. Callers hold <see cref="_actionLock"/>.
    /// </summary>
    private void TakeOverDelivery(AgentRole role)
    {
        Interlocked.Increment(ref role == AgentRole.Claude ? ref _claudeDeliveryEpoch : ref _codexDeliveryEpoch);

        if (_agentAdapterProvider.GetAdapter(role) is IWaitsOutQuotaLimits waitsOutQuota)
        {
            waitsOutQuota.ForgetAnnouncedQuotaWait();
        }
    }

    /// <summary>
    /// Nudges an agent that stopped short back into the work it was already
    /// doing, without advancing the cycle or resending the whole instruction.
    ///
    /// This is for the case the automatic paths cannot judge: an agent that ran,
    /// did real work, and stopped before finishing — a context limit, a refusal
    /// it recovered from, an operator's own Stop. Whether that work is worth
    /// continuing is a judgement about the repository, not about anything the
    /// bridge can observe, so it stays a button rather than a rule.
    /// </summary>
    private async Task ContinueAgentAsync(AgentRole role, CancellationToken cancellationToken)
    {
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var adapter = _agentAdapterProvider.GetAdapter(role);
            if (await adapter.IsProcessingAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"{role} is still working; there is nothing to continue until it stops.");
            }

            // Both routes end in the same waiting state they started from, so the
            // watchers and the completion probe carry on unchanged. Reaching that
            // state from Error also needs the run's resources back.
            var target = role == AgentRole.Claude ? BridgeState.WaitingForClaude : BridgeState.WaitingForCodex;
            if (!BridgeStateMachine.IsValidTransition(_stateMachine.Current, target))
            {
                throw new InvalidOperationException(
                    $"{role} cannot be continued from {_stateMachine.Current}.");
            }

            if (_runCts is null)
            {
                EnsureWatchers();
                _runCts = new CancellationTokenSource();
                _claudeWatcher!.Start();
                _codexWatcher!.Start();
            }

            TakeOverDelivery(role);
            _lastError = null;
            Transition(target, $"Continuing {role} for iteration {_currentIteration}");
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);

            if (role == AgentRole.Claude)
            {
                await InvokeClaudeAsync(cancellationToken, continueLastSession: true).ConfigureAwait(false);
            }
            else
            {
                await InvokeCodexAsync(cancellationToken, continueLastSession: true).ConfigureAwait(false);
            }
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public async Task ContinueWaitingForClaudeAsync(CancellationToken cancellationToken)
    {
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stateMachine.Current != BridgeState.Error
                || _lastError?.StartsWith("Failed to deliver instruction to Claude", StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException("Only a timed-out Claude delivery can be acknowledged.");
            }

            _lastError = null;
            _lastAction = $"Operator verified Claude delivery; waiting for iteration {_currentIteration} report";
            EnsureWatchers();
            _runCts ??= new CancellationTokenSource();
            Transition(BridgeState.WaitingForClaudeReport, _lastAction);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            _claudeWatcher!.Start();
            _codexWatcher!.Start();
        }
        finally
        {
            _actionLock.Release();
        }

        await _claudeWatcher!.CheckNowAsync(cancellationToken).ConfigureAwait(false);
        await _codexWatcher!.CheckNowAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ContinueWaitingForCodexAsync(CancellationToken cancellationToken)
    {
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stateMachine.Current != BridgeState.Error
                || _lastError?.StartsWith("Failed to deliver instruction to Codex", StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException("Only a timed-out Codex delivery can be acknowledged.");
            }

            _lastError = null;
            _lastAction = $"Operator verified Codex delivery; waiting for iteration {_currentIteration} prompt";
            EnsureWatchers();
            _runCts ??= new CancellationTokenSource();
            Transition(BridgeState.WaitingForCodexPrompt, _lastAction);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            _claudeWatcher!.Start();
            _codexWatcher!.Start();
        }
        finally
        {
            _actionLock.Release();
        }

        await _claudeWatcher!.CheckNowAsync(cancellationToken).ConfigureAwait(false);
        await _codexWatcher!.CheckNowAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stateMachine.Current is BridgeState.Idle or BridgeState.Stopped or BridgeState.Error or BridgeState.Paused)
            {
                _logger.LogInformation("Pause requested but bridge is {State}; ignoring.", _stateMachine.Current);
                return;
            }

            Transition(BridgeState.Paused, "Paused by user");
            _lastAction = "Paused by user";
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync("Agent Bridge", "Automation paused.", NotificationLevel.Info, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stateMachine.Current != BridgeState.Paused)
            {
                _logger.LogInformation("Resume requested but bridge is not paused (state={State}); ignoring.", _stateMachine.Current);
                return;
            }

            var target = _stateMachine.StateBeforePause ?? BridgeState.WaitingForClaudeReport;
            Transition(target, "Resumed by user");
            _lastAction = "Resumed by user";
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync("Agent Bridge", "Automation resumed.", NotificationLevel.Info, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actionLock.Release();
        }

        if (_claudeWatcher is not null) await _claudeWatcher.CheckNowAsync(cancellationToken).ConfigureAwait(false);
        if (_codexWatcher is not null) await _codexWatcher.CheckNowAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancellation must happen before waiting for the action lock. An agent
        // invocation holds that lock, so cancelling only after acquiring it would
        // make Stop wait for the full agent timeout.
        RequestRuntimeCancellation();
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopRuntimeResources();

            if (_stateMachine.Current == BridgeState.Stopped)
            {
                return;
            }

            Transition(BridgeState.Stopped, "Stopped by user");
            _lastAction = "Stopped by user";
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync("Agent Bridge", "Automation stopped.", NotificationLevel.Info, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public async Task ResetStateAsync(CancellationToken cancellationToken)
    {
        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _runCts?.Cancel();
            _claudeWatcher?.Stop();
            _claudeWatcher?.Dispose();
            _claudeWatcher = null;
            _codexWatcher?.Stop();
            _codexWatcher?.Dispose();
            _codexWatcher = null;

            await _stateStore.ResetAsync(cancellationToken).ConfigureAwait(false);
            InitializeFreshState();
            PublishStatus();
            _logger.LogInformation("Bridge state reset to a fresh Idle start.");
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public Task<bool> TestClaudeConnectionAsync(CancellationToken cancellationToken) =>
        TestConnectionAsync(AgentRole.Claude, cancellationToken);

    public Task<bool> TestCodexConnectionAsync(CancellationToken cancellationToken) =>
        TestConnectionAsync(AgentRole.Codex, cancellationToken);

    public async Task<bool> SendTestNotificationAsync(CancellationToken cancellationToken)
    {
        if (_notificationService is ISupportsNotificationTest testable)
        {
            return await testable.SendTestNotificationAsync(cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> TestConnectionAsync(AgentRole role, CancellationToken cancellationToken)
    {
        var adapter = _agentAdapterProvider.GetAdapter(role);
        if (!await adapter.IsApplicationRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await adapter.IsReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BridgeStatusView> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_configuration.ProjectPath))
        {
            try
            {
                _lastGitStatus = await _gitService.GetStatusAsync(_configuration.ProjectPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read git status for {Path}.", _configuration.ProjectPath);
            }
        }

        _lastClaudeAgentStatus = await SafeGetAgentStatusAsync(AgentRole.Claude, cancellationToken).ConfigureAwait(false);
        _lastCodexAgentStatus = await SafeGetAgentStatusAsync(AgentRole.Codex, cancellationToken).ConfigureAwait(false);

        return BuildStatusView();
    }

    private async Task<AgentStatus> SafeGetAgentStatusAsync(AgentRole role, CancellationToken cancellationToken)
    {
        try
        {
            var adapter = _agentAdapterProvider.GetAdapter(role);
            // The visible Stop control is the strongest live signal available from
            // both desktop agents. Check it first so the dashboard cannot report
            // Ready while an agent is actively generating a response.
            if (await adapter.IsProcessingAsync(cancellationToken).ConfigureAwait(false))
            {
                return AgentStatus.Busy;
            }

            return await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query {Role} status.", role);
            return AgentStatus.Unknown;
        }
    }

    // ------------------------------------------------------------------
    // File watcher event handlers
    // ------------------------------------------------------------------

    private void EnsureWatchers()
    {
        if (_claudeWatcher is not null && _codexWatcher is not null)
        {
            return;
        }

        var claudePath = _projectService.GetClaudeReportFilePath(_configuration);
        var codexPath = _projectService.GetCodexPromptFilePath(_configuration);

        _claudeWatcher = _watcherFactory.Create(claudePath);
        _codexWatcher = _watcherFactory.Create(codexPath);

        _claudeWatcher.StableChangeDetected += (_, e) => _ = HandleClaudeReportChangedAsync(e);
        _claudeWatcher.Error += OnWatcherError;

        _codexWatcher.StableChangeDetected += (_, e) => _ = HandleCodexPromptChangedAsync(e);
        _codexWatcher.Error += OnWatcherError;
    }

    private void OnWatcherError(object? sender, FileWatcherErrorEventArgs e)
    {
        _logger.LogWarning(e.Exception, "File watcher error for {FilePath}: {Message}", e.FilePath, e.Message);
    }

    private async Task HandleClaudeReportChangedAsync(StableFileChangedEventArgs e)
    {
        var token = _runCts?.Token ?? CancellationToken.None;
        try
        {
            await _actionLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_stateMachine.Current != BridgeState.WaitingForClaudeReport)
            {
                _logger.LogDebug("Ignoring {File} change: not currently waiting for it (state={State}).", e.FilePath, _stateMachine.Current);
                return;
            }

            if (await DeferFileWhileAgentIsProcessingAsync(
                    AgentRole.Claude, _claudeWatcher!, token).ConfigureAwait(false))
            {
                return;
            }

            if (string.Equals(e.ContentHashSha256, _lastClaudeReportHash, StringComparison.Ordinal))
            {
                NoteFileAlreadyHandled(AgentRole.Claude, _configuration.ClaudeReportFileName);
                return;
            }

            if (PredatesInstruction(AgentRole.Claude, e))
            {
                return;
            }

            if (TrailsTheOtherProtocolFile(AgentRole.Claude, e))
            {
                return;
            }

            var nextIteration = _currentIteration + 1;
            if (nextIteration > _configuration.MaximumIterations)
            {
                await StopForMaxIterationsAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _currentIteration = nextIteration;
            _lastClaudeReportHash = e.ContentHashSha256;
            // Claude delivered, so whatever allowance trouble preceded this is
            // over; the next stuck iteration starts its own count.
            _claudeQuotaResends = 0;
            _claudeFailedRunResends = 0;
            _lastClaudeReportUpdateUtc = e.DetectedAtUtc;
            _lastAgent = AgentRole.Claude;
            _lastAction = $"Claude report detected (iteration {_currentIteration})";
            Transition(BridgeState.ClaudeReportDetected, _lastAction);
            await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);

            await NotifyAgentFinishedAsync(
                AgentRole.Claude, _configuration.ClaudeReportFileName, e.Content).ConfigureAwait(false);

            Transition(BridgeState.WaitingForCodex, "Preparing to invoke Codex");
            await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);

            await InvokeCodexAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogInformation("Claude report processing cancelled because the bridge is stopping.");
        }
        catch (Exception ex)
        {
            await HandleUnexpectedErrorAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    private async Task HandleCodexPromptChangedAsync(StableFileChangedEventArgs e)
    {
        var token = _runCts?.Token ?? CancellationToken.None;
        try
        {
            await _actionLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_stateMachine.Current != BridgeState.WaitingForCodexPrompt)
            {
                _logger.LogDebug("Ignoring {File} change: not currently waiting for it (state={State}).", e.FilePath, _stateMachine.Current);
                return;
            }

            if (await DeferFileWhileAgentIsProcessingAsync(
                    AgentRole.Codex, _codexWatcher!, token).ConfigureAwait(false))
            {
                return;
            }

            if (string.Equals(e.ContentHashSha256, _lastCodexPromptHash, StringComparison.Ordinal))
            {
                NoteFileAlreadyHandled(AgentRole.Codex, _configuration.CodexPromptFileName);
                return;
            }

            if (PredatesInstruction(AgentRole.Codex, e))
            {
                return;
            }

            if (TrailsTheOtherProtocolFile(AgentRole.Codex, e))
            {
                return;
            }

            _lastCodexPromptHash = e.ContentHashSha256;
            _codexQuotaResends = 0;
            _codexFailedRunResends = 0;
            _lastCodexPromptUpdateUtc = e.DetectedAtUtc;
            _lastAgent = AgentRole.Codex;
            _lastAction = $"Codex prompt detected (iteration {_currentIteration})";
            Transition(BridgeState.CodexPromptDetected, _lastAction);
            await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);

            await NotifyAgentFinishedAsync(
                AgentRole.Codex, _configuration.CodexPromptFileName, e.Content).ConfigureAwait(false);

            Transition(BridgeState.WaitingForClaude, "Preparing to invoke Claude");
            await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);

            await InvokeClaudeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogInformation("Codex prompt processing cancelled because the bridge is stopping.");
        }
        catch (Exception ex)
        {
            await HandleUnexpectedErrorAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    // ------------------------------------------------------------------
    // Agent invocation
    // ------------------------------------------------------------------

    private async Task<bool> DeferFileWhileAgentIsProcessingAsync(
        AgentRole role,
        IFileWatcher watcher,
        CancellationToken cancellationToken)
    {
        var adapter = _agentAdapterProvider.GetAdapter(role);
        var isProcessing = await adapter.IsProcessingAsync(cancellationToken).ConfigureAwait(false);
        var status = isProcessing
            ? AgentStatus.Busy
            : await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var isWaitingForQuota = status == AgentStatus.RateLimited;
        var statusIsIndeterminate = status is not AgentStatus.Ready;
        if (!isProcessing && !isWaitingForQuota && !statusIsIndeterminate)
        {
            return false;
        }

        if (role == AgentRole.Claude)
        {
            _lastClaudeAgentStatus = status;
        }
        else
        {
            _lastCodexAgentStatus = status;
        }

        _lastAction = isWaitingForQuota
            ? $"{role} has no allowance left; waiting for the automatic reset"
            : !isProcessing
                ? $"{role} status could not be verified; protocol file deferred"
            : $"{role} is still processing; protocol file deferred";
        PublishStatus();

        ref var activeProbe = ref (role == AgentRole.Claude
            ? ref _claudeCompletionProbeActive
            : ref _codexCompletionProbeActive);
        if (Interlocked.Exchange(ref activeProbe, 1) == 0)
        {
            _ = WaitForAgentCompletionAndRecheckAsync(role, adapter, watcher, cancellationToken);
        }

        _logger.LogInformation(isWaitingForQuota
            ? "Deferring {Agent} protocol file: no allowance left; waiting for the automatic reset."
            : !isProcessing
                ? "Deferring {Agent} protocol file: its status is {Status}, so idle cannot be verified."
                : "Deferring {Agent} protocol file: the agent is still processing.", role, status);
        return true;
    }

    private async Task StartCompletionProbeIfAgentActiveAsync(
        AgentRole role,
        IFileWatcher watcher,
        CancellationToken cancellationToken)
    {
        var adapter = _agentAdapterProvider.GetAdapter(role);
        var processing = await adapter.IsProcessingAsync(cancellationToken).ConfigureAwait(false);
        var status = processing
            ? AgentStatus.Busy
            : await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var waitingForQuota = status == AgentStatus.RateLimited;
        var statusIsIndeterminate = status is not AgentStatus.Ready;
        if (!processing && !waitingForQuota && !statusIsIndeterminate)
        {
            return;
        }

        if (role == AgentRole.Claude)
        {
            _lastClaudeAgentStatus = status;
        }
        else
        {
            _lastCodexAgentStatus = status;
        }
        _lastAction = waitingForQuota
            ? $"{role} has no allowance left; waiting for the automatic reset"
            : $"{role} is still processing; waiting for completion";
        PublishStatus();

        ref var activeProbe = ref (role == AgentRole.Claude
            ? ref _claudeCompletionProbeActive
            : ref _codexCompletionProbeActive);
        if (Interlocked.Exchange(ref activeProbe, 1) == 0)
        {
            _ = WaitForAgentCompletionAndRecheckAsync(role, adapter, watcher, cancellationToken);
        }
    }

    private async Task WaitForAgentCompletionAndRecheckAsync(
        AgentRole role,
        IAgentAdapter adapter,
        IFileWatcher watcher,
        CancellationToken cancellationToken)
    {
        try
        {
            // One pass per delivery being watched. A resend after an exhausted
            // allowance is a new delivery, and this probe goes round again to
            // watch it: handing off to a fresh probe cannot work, because this one
            // still holds the flag that stops two from running at once, so the
            // resent run would go unwatched and the wait would never end.
            while (true)
            {
                // The delivery this pass is watching. If the operator takes over
                // with Continue or Retry, this moves, and the pass re-observes
                // the new delivery from the top rather than resending on top of it.
                var epoch = CurrentDeliveryEpoch(role);

                // Whether this pass ever actually saw the agent working. It is the
                // difference between "finished without doing its part" and "has not
                // started yet", and only the first is a fault. A desktop agent that
                // has not yet rendered its Stop control looks idle for a moment
                // after an instruction lands, and treating that as a failed
                // iteration would end runs that were about to succeed.
                var everObservedWorking = false;

                // Whether this pass sat out an exhausted allowance. An agent that
                // was refused produced nothing for a reason that fixes itself, so
                // the run resends once it is back rather than ending.
                var everWaitedForQuota = false;
                var announcedTheQuotaWait = false;

                while (true)
                {
                    if (CurrentDeliveryEpoch(role) != epoch)
                    {
                        // A manual Continue or Retry has replaced the delivery
                        // this inner loop was waiting on — including waiting out
                        // an allowance the operator has now worked around. Drop
                        // out and re-observe from the top.
                        break;
                    }

                    var processing = await adapter.IsProcessingAsync(cancellationToken).ConfigureAwait(false);
                    var status = processing
                        ? AgentStatus.Busy
                        : await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    var waitingForQuota = status == AgentStatus.RateLimited;
                    var statusIsIndeterminate = status is not AgentStatus.Ready;
                    if (!processing && !waitingForQuota && !statusIsIndeterminate)
                    {
                        break;
                    }

                    everObservedWorking |= processing;
                    everWaitedForQuota |= waitingForQuota;

                    if (waitingForQuota && !announcedTheQuotaWait)
                    {
                        announcedTheQuotaWait = true;
                        _logger.LogWarning("{Agent} has no allowance left; the run waits for its reset.", role);
                        await NotifyAsync(
                            $"Agent Bridge — {role} is out of allowance",
                            $"{role} has nothing left to spend. The run is paused and resumes by itself once the "
                            + "allowance resets; nothing needs to be restarted.",
                            NotificationLevel.Warning,
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (role == AgentRole.Claude)
                    {
                        _lastClaudeAgentStatus = status;
                    }
                    else
                    {
                        _lastCodexAgentStatus = status;
                    }
                    _lastAction = waitingForQuota
                        ? $"{role} has no allowance left; waiting for the automatic reset"
                        : !processing
                            ? $"{role} status could not be verified; waiting before file recheck"
                        : $"{role} resumed and is processing";
                    PublishStatus();
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }

                if (CurrentDeliveryEpoch(role) != epoch)
                {
                    // The operator took the delivery over while this pass was
                    // watching. Its own invocation has already started a fresh
                    // watch through StartCompletionWatchdog; loop round to pick
                    // up the new attempt rather than resending anything.
                    _logger.LogInformation("{Agent} delivery was taken over by the operator; re-observing.", role);
                    continue;
                }

                _logger.LogInformation("{Agent} processing finished; rechecking its protocol file.", role);
                await watcher.CheckNowAsync(cancellationToken).ConfigureAwait(false);

                if (everWaitedForQuota)
                {
                    if (await ResendAfterTheAllowanceResetAsync(role, epoch, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    break;
                }

                if (everObservedWorking)
                {
                    // An agent that stopped too soon to have worked did not have
                    // an empty iteration — it never got started. Loop round so the
                    // retry is watched the same way the first attempt was;
                    // resending with nothing observing it is how a retry silently
                    // becomes a stall.
                    if (await ResendAfterAFailedRunAsync(role, epoch, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await FailIfTheAgentProducedNothingAsync(role, epoch, cancellationToken).ConfigureAwait(false);
                }

                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop/pause lifecycle.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while waiting to recheck the {Agent} protocol file.", role);
        }
        finally
        {
            if (role == AgentRole.Claude)
            {
                Interlocked.Exchange(ref _claudeCompletionProbeActive, 0);
            }
            else
            {
                Interlocked.Exchange(ref _codexCompletionProbeActive, 0);
            }
        }
    }

    /// <summary>
    /// Starts watching the agent an instruction was just delivered to, so that
    /// its finishing is noticed whether or not it writes anything. The existing
    /// probes only start once a protocol file has already appeared, which is
    /// exactly the case that cannot happen when an agent produces nothing.
    /// </summary>
    private void StartCompletionWatchdog(AgentRole role, IAgentAdapter adapter)
    {
        if (_configuration.DryRun)
        {
            return;
        }

        var watcher = role == AgentRole.Claude ? _claudeWatcher : _codexWatcher;
        if (watcher is null)
        {
            return;
        }

        ref var activeProbe = ref (role == AgentRole.Claude
            ? ref _claudeCompletionProbeActive
            : ref _codexCompletionProbeActive);
        if (Interlocked.Exchange(ref activeProbe, 1) == 0)
        {
            // The run's own token, not the caller's: this outlives the file event
            // that started the iteration, and only a stop should end it.
            _ = WaitForAgentCompletionAndRecheckAsync(
                role, adapter, watcher, _runCts?.Token ?? CancellationToken.None);
        }
    }

    // An allowance that resets and is immediately spent again would otherwise
    // resend forever. Five attempts is enough to cross a normal reset window and
    // few enough that a genuinely exhausted account stops instead of looping.
    private const int MaximumQuotaResends = 5;

    /// <summary>
    /// Resends the instruction an agent never got to act on because it had no
    /// allowance left.
    ///
    /// The refused run exited having written nothing, so there is no file coming
    /// and nothing to wait for — but the cause fixes itself, which is exactly the
    /// case that should not end a run. Waiting out the reset and sending the same
    /// instruction again is what makes an overnight run survive a limit it hit at
    /// two in the morning.
    /// </summary>
    /// <returns>True when a new delivery was made and is worth watching.</returns>
    // Far smaller than the allowance budget, and deliberately. An allowance
    // refusal states when it lifts, so waiting it out is informed; this is a
    // guess that the trouble has passed. Two attempts cover the transient case
    // without hammering a wall that is not going to move.
    private const int MaximumFailedRunResends = 2;

    // Long enough for the state behind an instant refusal — a stale token, a
    // connection that dropped — to have moved on, short enough that an unattended
    // run does not lose its night to it.
    private static readonly TimeSpan FailedRunRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resends the current instruction when the agent stopped so quickly that it
    /// cannot have started the work.
    ///
    /// A real run against F:\Rasta was ended by exactly this: the moment an
    /// allowance reset, the first invocation came back "403 Request not allowed"
    /// in under a second, from a cached authentication state that had not caught
    /// up. Nothing was wrong by the time anyone looked, but the loop had already
    /// stopped for the night.
    ///
    /// Called from the completion probe with no lock held, so that returning true
    /// lets the probe loop round and watch the retry. A retry nothing is watching
    /// is how one silently becomes a stall.
    /// </summary>
    private async Task<bool> ResendAfterAFailedRunAsync(
        AgentRole role, long expectedEpoch, CancellationToken cancellationToken)
    {
        var adapter = _agentAdapterProvider.GetAdapter(role);
        if (adapter is not IReportsRunOutcome { LastRunFailedWithoutWorking: true })
        {
            return false;
        }

        if (CurrentDeliveryEpoch(role) != expectedEpoch)
        {
            // The operator has taken this delivery over by hand; its own watch
            // is now in charge.
            return false;
        }

        ref var resends = ref (role == AgentRole.Claude ? ref _claudeFailedRunResends : ref _codexFailedRunResends);
        if (resends >= MaximumFailedRunResends)
        {
            _logger.LogWarning(
                "{Agent} has now stopped before starting {Attempts} times running on iteration {Iteration}. "
                + "Treating it as a real failure rather than resending again.",
                role, resends, _currentIteration);
            return false;
        }

        resends++;
        _logger.LogInformation(
            "{Agent} stopped before it could do any work; resending the iteration {Iteration} instruction "
            + "in {Delay}s (attempt {Attempt} of {Max}).",
            role, _currentIteration, FailedRunRetryDelay.TotalSeconds, resends, MaximumFailedRunResends);
        _lastAction = $"{role} stopped before starting; retrying iteration {_currentIteration} shortly";
        PublishStatus();

        await Task.Delay(FailedRunRetryDelay, cancellationToken).ConfigureAwait(false);

        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The wait is long enough for the picture to have changed underneath
            // it: the file may have arrived late, or the operator may have stopped
            // the run. Either way there is nothing left to retry.
            var waitingState = role == AgentRole.Claude
                ? BridgeState.WaitingForClaudeReport
                : BridgeState.WaitingForCodexPrompt;
            if (_stateMachine.Current != waitingState || CurrentDeliveryEpoch(role) != expectedEpoch)
            {
                return false;
            }

            if (role == AgentRole.Claude)
            {
                Transition(BridgeState.WaitingForClaude, $"Retrying Claude instruction for iteration {_currentIteration}");
                await InvokeClaudeAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Transition(BridgeState.WaitingForCodex, $"Retrying Codex instruction for iteration {_currentIteration}");
                await InvokeCodexAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _actionLock.Release();
        }
    }

    private async Task<bool> ResendAfterTheAllowanceResetAsync(
        AgentRole role, long expectedEpoch, CancellationToken cancellationToken)
    {
        await Task.Delay(FileArrivalGrace, cancellationToken).ConfigureAwait(false);

        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var waitingState = role == AgentRole.Claude
                ? BridgeState.WaitingForClaudeReport
                : BridgeState.WaitingForCodexPrompt;

            // The agent did deliver after all, or the operator intervened.
            if (_stateMachine.Current != waitingState || CurrentDeliveryEpoch(role) != expectedEpoch)
            {
                return false;
            }

            ref var resends = ref (role == AgentRole.Claude ? ref _claudeQuotaResends : ref _codexQuotaResends);
            if (resends >= MaximumQuotaResends)
            {
                StopRuntimeResources();
                SetError(
                    $"{role} ran out of allowance {resends} times in a row on iteration {_currentIteration} and "
                    + "still could not do the work. The run is stopped rather than resending again.");
                Transition(BridgeState.Error, _lastError!);
                await PersistStateAsync(cancellationToken).ConfigureAwait(false);
                await NotifyAsync(
                    $"Agent Bridge — {role} is still out of allowance", _lastError!,
                    NotificationLevel.Error, cancellationToken).ConfigureAwait(false);
                return false;
            }

            resends++;
            _logger.LogInformation(
                "{Agent} allowance has reset; resending the iteration {Iteration} instruction (attempt {Attempt}).",
                role, _currentIteration, resends);
            await NotifyAsync(
                $"Agent Bridge — {role} is back",
                $"The allowance reset. Resending the iteration {_currentIteration} instruction.",
                NotificationLevel.Info, cancellationToken).ConfigureAwait(false);

            if (role == AgentRole.Claude)
            {
                Transition(BridgeState.WaitingForClaude, $"Resending Claude instruction for iteration {_currentIteration}");
                await InvokeClaudeAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Transition(BridgeState.WaitingForCodex, $"Resending Codex instruction for iteration {_currentIteration}");
                await InvokeCodexAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _actionLock.Release();
        }
    }

    /// <summary>
    /// Ends the run when an agent that was working has stopped without leaving a
    /// new protocol file behind.
    ///
    /// Nothing else notices this. Both waits are passive — the bridge sits on a
    /// file watcher — so an agent that exits cleanly having written nothing puts
    /// the run into a wait that no event will ever end. Unattended, that is
    /// indistinguishable from an agent still thinking, and a real run has sat in
    /// it for hours. An explicit Error at least says which agent stopped and what
    /// file it did not write.
    /// </summary>
    private async Task FailIfTheAgentProducedNothingAsync(
        AgentRole role, long expectedEpoch, CancellationToken cancellationToken)
    {
        // The recheck above can raise a change whose handler is still running.
        // Concluding before it has had its chance would report a failure for a
        // file that arrived.
        await Task.Delay(FileArrivalGrace, cancellationToken).ConfigureAwait(false);

        await _actionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (waitingState, sentAtUtc, fileName) = role == AgentRole.Claude
                ? (BridgeState.WaitingForClaudeReport, _claudeInstructionSentAtUtc, _configuration.ClaudeReportFileName)
                : (BridgeState.WaitingForCodexPrompt, _codexInstructionSentAtUtc, _configuration.CodexPromptFileName);

            // Still waiting on the very instruction this probe was watching. Any
            // other state — the file arrived, the operator paused or took the
            // delivery over, the run was stopped — means there is nothing to
            // report.
            if (_stateMachine.Current != waitingState
                || sentAtUtc is null
                || CurrentDeliveryEpoch(role) != expectedEpoch)
            {
                return;
            }

            StopRuntimeResources();
            SetError(
                $"{role} finished without updating {fileName}, so iteration {_currentIteration} produced nothing "
                + $"to hand on. Its run started at {sentAtUtc:u}. Check the agent's own output in the log: a "
                + "permission it could not be granted unattended is the usual cause.");
            Transition(BridgeState.Error, _lastError!);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync(
                $"Agent Bridge — {role} produced nothing", _lastError!, NotificationLevel.Error, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    private async Task InvokeCodexAsync(CancellationToken cancellationToken, bool continueLastSession = false)
    {
        Transition(
            BridgeState.CodexProcessing,
            continueLastSession
                ? $"Asking Codex to continue iteration {_currentIteration}"
                : $"Invoking Codex for iteration {_currentIteration}");
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);

        var branch = await SafeRefreshGitStatusAsync(cancellationToken).ConfigureAwait(false);
        var variables = TemplateVariableBuilder.Build(
            _configuration.ProjectPath, _currentIteration, _configuration.MaximumIterations,
            _configuration.ClaudeReportFileName, _configuration.CodexPromptFileName,
            branch, _lastClaudeReportHash, _lastCodexPromptHash);
        var message = continueLastSession
            ? ContinueInstruction
            : _templateEngine.Render(_configuration.CodexInstructionTemplate, variables);

        var adapter = _agentAdapterProvider.GetAdapter(AgentRole.Codex);
        var success = await InvokeAgentAsync(adapter, message, continueLastSession, cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            SetError($"Failed to deliver instruction to Codex for iteration {_currentIteration}.");
            Transition(BridgeState.Error, _lastError!);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync("Agent Bridge — Codex unreachable", _lastError!, NotificationLevel.Error, cancellationToken).ConfigureAwait(false);
            return;
        }

        _codexInstructionSentAtUtc = DateTimeOffset.UtcNow;
        StartCompletionWatchdog(AgentRole.Codex, adapter);
        _lastAgent = AgentRole.Codex;
        _lastAction = _configuration.DryRun
            ? $"[Dry Run] Would send instruction to Codex for iteration {_currentIteration}"
            : $"Sent instruction to Codex for iteration {_currentIteration}";
        Transition(BridgeState.WaitingForCodexPrompt, _lastAction);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync("Agent Bridge", $"Codex is reviewing iteration {_currentIteration}.", NotificationLevel.Info, cancellationToken).ConfigureAwait(false);
    }

    private async Task InvokeClaudeAsync(CancellationToken cancellationToken, bool continueLastSession = false)
    {
        Transition(
            BridgeState.ClaudeProcessing,
            continueLastSession
                ? $"Asking Claude to continue iteration {_currentIteration}"
                : $"Invoking Claude for iteration {_currentIteration}");
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);

        var branch = await SafeRefreshGitStatusAsync(cancellationToken).ConfigureAwait(false);
        var variables = TemplateVariableBuilder.Build(
            _configuration.ProjectPath, _currentIteration, _configuration.MaximumIterations,
            _configuration.ClaudeReportFileName, _configuration.CodexPromptFileName,
            branch, _lastClaudeReportHash, _lastCodexPromptHash);
        var message = continueLastSession
            ? ContinueInstruction
            : _templateEngine.Render(_configuration.ClaudeInstructionTemplate, variables);

        var adapter = _agentAdapterProvider.GetAdapter(AgentRole.Claude);
        var success = await InvokeAgentAsync(adapter, message, continueLastSession, cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            SetError($"Failed to deliver instruction to Claude for iteration {_currentIteration}.");
            Transition(BridgeState.Error, _lastError!);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync("Agent Bridge — Claude unreachable", _lastError!, NotificationLevel.Error, cancellationToken).ConfigureAwait(false);
            return;
        }

        _claudeInstructionSentAtUtc = DateTimeOffset.UtcNow;
        StartCompletionWatchdog(AgentRole.Claude, adapter);
        _lastAgent = AgentRole.Claude;
        _lastAction = _configuration.DryRun
            ? $"[Dry Run] Would send instruction to Claude for iteration {_currentIteration}"
            : $"Sent instruction to Claude for iteration {_currentIteration}";
        Transition(BridgeState.WaitingForClaudeReport, _lastAction);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync("Agent Bridge", $"Claude is implementing iteration {_currentIteration + 1}.", NotificationLevel.Info, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> InvokeAgentAsync(
        IAgentAdapter adapter, string message, bool continueLastSession, CancellationToken cancellationToken)
    {
        if (_configuration.DryRun)
        {
            _logger.LogInformation(
                "[Dry Run] Would activate {Agent}, find its conversation and input box, then send:\n{Message}",
                adapter.Name, message);
            return true;
        }

        // This bounds one whole delivery, which internally waits for focus, for
        // the editor to report the draft back, and for a send receipt. Set below
        // those waits it does not make delivery fail fast, it makes it impossible
        // — every attempt is cancelled mid-flight. Say so rather than overriding
        // the operator, who may well want a short deadline on a fast machine.
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _configuration.AgentTimeoutSeconds));
        if (timeout < TypicalDeliveryTimeout)
        {
            _logger.LogWarning(
                "Agent timeout is {Configured}s. One delivery to {Agent} can legitimately take up to about "
                + "{Typical}s when the desktop session is disconnected, and will be cancelled before it "
                + "finishes. Raise Agent timeout in Settings if deliveries keep timing out.",
                timeout.TotalSeconds, adapter.Name, TypicalDeliveryTimeout.TotalSeconds);
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token, _runCts?.Token ?? CancellationToken.None);
        var token = linkedCts.Token;
        var retryOptions = RetryOptions.FromConfiguration(_configuration);

        try
        {
            var running = await _retryPolicy.ExecuteUntilTrueAsync(
                t => adapter.IsApplicationRunningAsync(t), retryOptions, token).ConfigureAwait(false);
            if (!running)
            {
                var autoLaunch = adapter.Role == AgentRole.Claude ? _configuration.AutoLaunchClaude : _configuration.AutoLaunchChatGpt;
                if (!autoLaunch || !await adapter.LaunchApplicationAsync(token).ConfigureAwait(false))
                {
                    _logger.LogWarning("{Agent} is not running and auto-launch is disabled or failed.", adapter.Name);
                    return false;
                }

                running = await _retryPolicy.ExecuteUntilTrueAsync(
                    t => adapter.IsApplicationRunningAsync(t), retryOptions, token).ConfigureAwait(false);
                if (!running)
                {
                    _logger.LogWarning("{Agent} did not become available after launch.", adapter.Name);
                    return false;
                }
            }

            if (!await _retryPolicy.ExecuteUntilTrueAsync(t => adapter.ActivateAsync(t), retryOptions, token).ConfigureAwait(false)) return false;
            if (!await _retryPolicy.ExecuteUntilTrueAsync(t => adapter.IsReadyAsync(t), retryOptions, token).ConfigureAwait(false)) return false;
            if (!await _retryPolicy.ExecuteUntilTrueAsync(t => adapter.FindConversationAsync(t), retryOptions, token).ConfigureAwait(false)) return false;

            // A conversation that was only reachable from the sidebar could not be
            // observed for activity until it was opened above. Now that it is on
            // screen, refuse to type into it while it is still streaming.
            if (!await _retryPolicy.ExecuteUntilTrueAsync(
                    async t => !await adapter.IsProcessingAsync(t).ConfigureAwait(false),
                    retryOptions, token).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "{Agent} is still processing in the configured conversation; not delivering this instruction.",
                    adapter.Name);
                return false;
            }

            if (!await _retryPolicy.ExecuteUntilTrueAsync(t => adapter.FindInputBoxAsync(t), retryOptions, token).ConfigureAwait(false)) return false;

            // A desktop window already *is* the conversation, so typing into it
            // continues by itself. Only a command line needs to be told to bring
            // the session back.
            return continueLastSession && adapter is IContinuesItsLastSession resuming
                ? await resuming.ContinueLastSessionAsync(message, token).ConfigureAwait(false)
                : await adapter.SendMessageAsync(message, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timed out waiting for {Agent} after {Timeout}s.", adapter.Name, _configuration.AgentTimeoutSeconds);
            return false;
        }
    }

    private async Task StopForMaxIterationsAsync(CancellationToken cancellationToken)
    {
        _lastAction = $"Maximum iterations ({_configuration.MaximumIterations}) reached.";
        StopRuntimeResources();
        Transition(BridgeState.Stopped, _lastAction);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync("Agent Bridge — stopped", _lastAction, NotificationLevel.Warning, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("{Message}", _lastAction);
    }

    private async Task HandleUnexpectedErrorAsync(Exception ex)
    {
        _logger.LogError(ex, "Unexpected orchestration error.");
        SetError($"Unexpected error: {ex.Message}");
        if (BridgeStateMachine.IsValidTransition(_stateMachine.Current, BridgeState.Error))
        {
            Transition(BridgeState.Error, _lastError!);
        }

        await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);
        await NotifyAsync("Agent Bridge — error", _lastError!, NotificationLevel.Error, CancellationToken.None).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// True when the detected file was already on disk before this run handed the
    /// agent its instruction, and therefore cannot be the answer to it.
    ///
    /// Without this, a protocol file that has not been rewritten for hours is
    /// accepted as a fresh reply the moment its hash first differs from the
    /// persisted one — which is exactly what happens on a first run, where that
    /// persisted hash is still null. The bridge then forwards stale work and the
    /// other agent redoes something it already finished.
    ///
    /// The gate only applies while an instruction from this run is outstanding.
    /// After a restart nothing is outstanding, so a file written while the bridge
    /// was down is still consumed on its hash alone.
    /// </summary>
    /// <summary>
    /// Says, where the operator can see it, that the file this state is waiting
    /// for is byte-for-byte the one already acted on.
    ///
    /// This used to be a debug line, which the hosts do not emit: the bridge sat
    /// in a waiting state with nothing on screen and nothing in the log, and the
    /// only way to learn why was to read the persisted hashes by hand. It is a
    /// dead end rather than a pause — the other agent writes its file only in
    /// answer to this one — so it has to be visible and it has to say what fixes
    /// it.
    /// </summary>
    private void NoteFileAlreadyHandled(AgentRole role, string fileName)
    {
        _lastAction =
            $"{fileName} is unchanged since it was last acted on, so it was not handled again. "
            + $"Reset the bridge state to run it again, or wait for {role} to write a new one.";
        _logger.LogInformation(
            "Ignoring {File}: its content is identical to the revision already handled, so there is nothing new "
            + "to act on. Reset the bridge state to act on it again, or wait for a new revision.", fileName);
        PublishStatus();
    }

    /// <summary>
    /// True when the file that just changed is older than the other protocol
    /// file, which means it belongs to an earlier cycle rather than answering the
    /// current one.
    ///
    /// The protocol strictly alternates — Codex writes a prompt, Claude answers
    /// with a report, Codex answers that with the next prompt — so a genuine
    /// reply is always newer than what it is replying to. A report older than the
    /// current prompt is a leftover: most often one from before a run that timed
    /// out or was reset without updating it. Unlike the hash and instruction-time
    /// guards, this one reads the files themselves and so keeps working across a
    /// restart, when no instruction is outstanding and every in-memory timestamp
    /// has been reset.
    /// </summary>
    private bool TrailsTheOtherProtocolFile(AgentRole role, StableFileChangedEventArgs e)
    {
        var (counterpartPath, counterpartRole) = role == AgentRole.Claude
            ? (_projectService.GetCodexPromptFilePath(_configuration), AgentRole.Codex)
            : (_projectService.GetClaudeReportFilePath(_configuration), AgentRole.Claude);

        var counterpartWrittenUtc = _projectService.GetLastWriteTimeUtc(counterpartPath);
        if (counterpartWrittenUtc is null || e.LastWriteTimeUtc > counterpartWrittenUtc)
        {
            return false;
        }

        var fileName = Path.GetFileName(e.FilePath);
        _lastAction =
            $"{fileName} is older than {counterpartRole}'s file, so it is a leftover from an earlier cycle, "
            + $"not a reply to the current one. Waiting for {role} to write a new one.";
        _logger.LogInformation(
            "Ignoring {File}: written {WrittenAt:O}, before {Counterpart} at {CounterpartAt:O}. It answers a "
            + "previous cycle, so {Agent} has not replied to the current one yet.",
            e.FilePath, e.LastWriteTimeUtc, counterpartRole, counterpartWrittenUtc, role);
        PublishStatus();
        return true;
    }

    private bool PredatesInstruction(AgentRole role, StableFileChangedEventArgs e)
    {
        var sentAtUtc = role == AgentRole.Claude
            ? _claudeInstructionSentAtUtc
            : _codexInstructionSentAtUtc;
        if (sentAtUtc is null || e.LastWriteTimeUtc >= sentAtUtc)
        {
            return false;
        }

        _logger.LogInformation(
            "Ignoring {File}: it was last written {WrittenAt:O}, before the {Agent} instruction was delivered at {SentAt:O}, "
            + "so it cannot be the reply to it.",
            e.FilePath, e.LastWriteTimeUtc, role, sentAtUtc);
        return true;
    }

    private void ApplyRecoveredState(BridgeStateSnapshot snapshot)
    {
        _currentIteration = snapshot.CurrentIteration;
        _lastClaudeReportHash = snapshot.LastClaudeReportHash;
        _lastCodexPromptHash = snapshot.LastCodexPromptHash;
        _lastClaudeReportUpdateUtc = snapshot.LastClaudeReportUpdateUtc;
        _lastCodexPromptUpdateUtc = snapshot.LastCodexPromptUpdateUtc;
        _lastAgent = snapshot.LastAgent;
        _lastAction = snapshot.LastAction;
        _lastError = snapshot.LastError;
        _startedAtUtc = snapshot.StartedAtUtc;

        var safeToResume = snapshot.CurrentState is
            BridgeState.Idle or BridgeState.Stopped or BridgeState.Paused or
            BridgeState.WaitingForClaudeReport or BridgeState.WaitingForCodexPrompt or
            BridgeState.Error;

        if (!safeToResume)
        {
            _stateMachine.ForceState(BridgeState.Error, null);
            SetError(
                $"Ambiguous state '{snapshot.CurrentState}' found on restart — an agent action may have been " +
                "in flight when the application stopped. Call ResetState to start a fresh run.");
            return;
        }

        _stateMachine.ForceState(snapshot.CurrentState, snapshot.StateBeforePause);
    }

    private void InitializeFreshState()
    {
        _currentIteration = 0;
        _lastClaudeReportHash = null;
        _lastCodexPromptHash = null;
        _claudeInstructionSentAtUtc = null;
        _codexInstructionSentAtUtc = null;
        _lastClaudeReportUpdateUtc = null;
        _lastCodexPromptUpdateUtc = null;
        _lastAgent = null;
        _lastAction = null;
        _lastError = null;
        _startedAtUtc = null;
        _stateMachine.ForceState(BridgeState.Idle, null);
    }

    private void SetError(string message)
    {
        _lastError = message;
        _logger.LogError("{Message}", message);
    }

    private void StopRuntimeResources()
    {
        var runCts = Interlocked.Exchange(ref _runCts, null);
        if (runCts is not null)
        {
            runCts.Cancel();
            runCts.Dispose();
        }

        _claudeWatcher?.Stop();
        _codexWatcher?.Stop();
    }

    private void RequestRuntimeCancellation()
    {
        try
        {
            Volatile.Read(ref _runCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent stop/dispose already completed cancellation.
        }
    }

    private void Transition(BridgeState to, string reason)
    {
        var now = _timeProvider.GetUtcNow();
        var from = _stateMachine.Current;
        if (!_stateMachine.TryTransition(to, reason, now))
        {
            _logger.LogError("Illegal transition attempted: {From} -> {To} ({Reason})", from, to, reason);
            throw new InvalidStateTransitionException(from, to);
        }

        _logger.LogInformation("State {From} -> {To}: {Reason}", from, to, reason);
        PublishStatus();
    }

    private async Task<string?> SafeRefreshGitStatusAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configuration.ProjectPath))
        {
            return null;
        }

        try
        {
            _lastGitStatus = await _gitService.GetStatusAsync(_configuration.ProjectPath, cancellationToken).ConfigureAwait(false);
            return _lastGitStatus.CurrentBranch;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve current git branch.");
            return null;
        }
    }

    private async Task PersistStateAsync(CancellationToken cancellationToken)
    {
        var snapshot = new BridgeStateSnapshot
        {
            CurrentState = _stateMachine.Current,
            StateBeforePause = _stateMachine.StateBeforePause,
            CurrentIteration = _currentIteration,
            MaximumIterations = _configuration.MaximumIterations,
            LastClaudeReportHash = _lastClaudeReportHash,
            LastCodexPromptHash = _lastCodexPromptHash,
            LastAgent = _lastAgent,
            LastAction = _lastAction,
            LastError = _lastError,
            LastClaudeReportUpdateUtc = _lastClaudeReportUpdateUtc,
            LastCodexPromptUpdateUtc = _lastCodexPromptUpdateUtc,
            StartedAtUtc = _startedAtUtc,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };

        await _stateStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Announces that an agent has delivered its protocol file, with the file
    /// itself attached for any channel that can carry it. This is the message an
    /// operator who has walked away actually wants — "iteration 3 is done, here
    /// is what it says".
    /// </summary>
    private Task NotifyAgentFinishedAsync(AgentRole role, string fileName, string content)
    {
        var branch = _lastGitStatus?.CurrentBranch;
        var where = string.IsNullOrWhiteSpace(branch) ? string.Empty : $" on {branch}";
        var counterpart = role == AgentRole.Claude ? "Codex" : "Claude";

        return NotifyAsync(
            $"{role} finished iteration {_currentIteration}",
            $"{role} delivered {fileName}{where}. {counterpart} is next. The file is attached.",
            NotificationLevel.Info,
            CancellationToken.None,
            new NotificationAttachment { FileName = fileName, Content = content });
    }

    private async Task NotifyAsync(
        string title,
        string message,
        NotificationLevel level,
        CancellationToken cancellationToken,
        NotificationAttachment? attachment = null)
    {
        if (!_configuration.NotificationsEnabled)
        {
            return;
        }

        try
        {
            await _notificationService.NotifyAsync(title, message, level, cancellationToken, attachment).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification.");
        }
    }

    private BridgeStatusView BuildStatusView()
    {
        var state = _stateMachine.Current;
        return new BridgeStatusView
        {
            CurrentState = state,
            StatusText = BridgeStateDescriptions.Describe(state),
            CurrentIteration = _currentIteration,
            MaximumIterations = _configuration.MaximumIterations,
            ClaudeStatus = _lastClaudeAgentStatus,
            CodexStatus = _lastCodexAgentStatus,
            IsRunning = state is not (BridgeState.Idle or BridgeState.Stopped or BridgeState.Paused or BridgeState.Error),
            IsPaused = state == BridgeState.Paused,
            LastAction = _lastAction,
            LastError = _lastError,
            LastClaudeReportUpdateUtc = _lastClaudeReportUpdateUtc,
            LastCodexPromptUpdateUtc = _lastCodexPromptUpdateUtc,
            GitBranch = _lastGitStatus?.CurrentBranch,
            GitWorkingTreeSummary = _lastGitStatus is null
                ? null
                : _lastGitStatus.IsWorkingTreeClean ? "Clean" : $"{_lastGitStatus.ModifiedFiles.Count} modified file(s)",
            DryRun = _configuration.DryRun,
            GeneratedAtUtc = _timeProvider.GetUtcNow(),
        };
    }

    private void PublishStatus()
    {
        StatusChanged?.Invoke(this, BuildStatusView());
    }

    private int _disposed;

    /// <summary>
    /// Idempotent by design: the DI container can end up tracking this instance for
    /// disposal under both its concrete registration and the IOrchestratorService
    /// factory registration that returns the same instance, which calls Dispose
    /// twice. A non-idempotent Dispose would throw ObjectDisposedException on the
    /// second call and crash shutdown — never acceptable per the "never crash the
    /// application" rule.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopRuntimeResources();
        _claudeWatcher?.Dispose();
        _codexWatcher?.Dispose();
        _actionLock.Dispose();
    }
}
