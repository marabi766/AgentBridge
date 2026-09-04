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
    private DateTimeOffset? _startedAtUtc;
    private AgentRole? _lastAgent;
    private string? _lastAction;
    private string? _lastError;
    private GitRepositoryStatus? _lastGitStatus;
    private AgentStatus _lastClaudeAgentStatus = AgentStatus.Unknown;
    private AgentStatus _lastCodexAgentStatus = AgentStatus.Unknown;
    private int _claudeCompletionProbeActive;
    private int _codexCompletionProbeActive;

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
            if (_stateMachine.Current != BridgeState.WaitingForCodexPrompt && !retryableError)
            {
                throw new InvalidOperationException("Codex delivery can only be retried while waiting for its prompt or after a delivery failure.");
            }

            if (_currentIteration < 1 || string.IsNullOrWhiteSpace(_lastClaudeReportHash))
            {
                throw new InvalidOperationException("There is no verified Claude report available to resend.");
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
            return await _agentAdapterProvider.GetAdapter(role).GetStatusAsync(cancellationToken).ConfigureAwait(false);
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
                _logger.LogDebug("Ignoring duplicate ClaudeResultReport.md hash {Hash}.", e.ContentHashSha256);
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
            _lastClaudeReportUpdateUtc = e.DetectedAtUtc;
            _lastAgent = AgentRole.Claude;
            _lastAction = $"Claude report detected (iteration {_currentIteration})";
            Transition(BridgeState.ClaudeReportDetected, _lastAction);
            await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);

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
                _logger.LogDebug("Ignoring duplicate CodexPrompt.md hash {Hash}.", e.ContentHashSha256);
                return;
            }

            _lastCodexPromptHash = e.ContentHashSha256;
            _lastCodexPromptUpdateUtc = e.DetectedAtUtc;
            _lastAgent = AgentRole.Codex;
            _lastAction = $"Codex prompt detected (iteration {_currentIteration})";
            Transition(BridgeState.CodexPromptDetected, _lastAction);
            await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);

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
        if (!await adapter.IsProcessingAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        ref var activeProbe = ref (role == AgentRole.Claude
            ? ref _claudeCompletionProbeActive
            : ref _codexCompletionProbeActive);
        if (Interlocked.Exchange(ref activeProbe, 1) == 0)
        {
            _ = WaitForAgentCompletionAndRecheckAsync(role, adapter, watcher, cancellationToken);
        }

        _logger.LogInformation(
            "Deferring {Agent} protocol file: the desktop agent is still processing.",
            role);
        return true;
    }

    private async Task WaitForAgentCompletionAndRecheckAsync(
        AgentRole role,
        IAgentAdapter adapter,
        IFileWatcher watcher,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await adapter.IsProcessingAsync(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("{Agent} processing finished; rechecking its protocol file.", role);
            await watcher.CheckNowAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task InvokeCodexAsync(CancellationToken cancellationToken)
    {
        Transition(BridgeState.CodexProcessing, $"Invoking Codex for iteration {_currentIteration}");
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);

        var branch = await SafeRefreshGitStatusAsync(cancellationToken).ConfigureAwait(false);
        var variables = TemplateVariableBuilder.Build(
            _configuration.ProjectPath, _currentIteration, _configuration.MaximumIterations,
            _configuration.ClaudeReportFileName, _configuration.CodexPromptFileName,
            branch, _lastClaudeReportHash, _lastCodexPromptHash);
        var message = _templateEngine.Render(_configuration.CodexInstructionTemplate, variables);

        var adapter = _agentAdapterProvider.GetAdapter(AgentRole.Codex);
        var success = await InvokeAgentAsync(adapter, message, cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            SetError($"Failed to deliver instruction to Codex for iteration {_currentIteration}.");
            Transition(BridgeState.Error, _lastError!);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync("Agent Bridge — Codex unreachable", _lastError!, NotificationLevel.Error, cancellationToken).ConfigureAwait(false);
            return;
        }

        _lastAgent = AgentRole.Codex;
        _lastAction = _configuration.DryRun
            ? $"[Dry Run] Would send instruction to Codex for iteration {_currentIteration}"
            : $"Sent instruction to Codex for iteration {_currentIteration}";
        Transition(BridgeState.WaitingForCodexPrompt, _lastAction);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync("Agent Bridge", $"Codex is reviewing iteration {_currentIteration}.", NotificationLevel.Info, cancellationToken).ConfigureAwait(false);
    }

    private async Task InvokeClaudeAsync(CancellationToken cancellationToken)
    {
        Transition(BridgeState.ClaudeProcessing, $"Invoking Claude for iteration {_currentIteration}");
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);

        var branch = await SafeRefreshGitStatusAsync(cancellationToken).ConfigureAwait(false);
        var variables = TemplateVariableBuilder.Build(
            _configuration.ProjectPath, _currentIteration, _configuration.MaximumIterations,
            _configuration.ClaudeReportFileName, _configuration.CodexPromptFileName,
            branch, _lastClaudeReportHash, _lastCodexPromptHash);
        var message = _templateEngine.Render(_configuration.ClaudeInstructionTemplate, variables);

        var adapter = _agentAdapterProvider.GetAdapter(AgentRole.Claude);
        var success = await InvokeAgentAsync(adapter, message, cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            SetError($"Failed to deliver instruction to Claude for iteration {_currentIteration}.");
            Transition(BridgeState.Error, _lastError!);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync("Agent Bridge — Claude unreachable", _lastError!, NotificationLevel.Error, cancellationToken).ConfigureAwait(false);
            return;
        }

        _lastAgent = AgentRole.Claude;
        _lastAction = _configuration.DryRun
            ? $"[Dry Run] Would send instruction to Claude for iteration {_currentIteration}"
            : $"Sent instruction to Claude for iteration {_currentIteration}";
        Transition(BridgeState.WaitingForClaudeReport, _lastAction);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync("Agent Bridge", $"Claude is implementing iteration {_currentIteration + 1}.", NotificationLevel.Info, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> InvokeAgentAsync(IAgentAdapter adapter, string message, CancellationToken cancellationToken)
    {
        if (_configuration.DryRun)
        {
            _logger.LogInformation(
                "[Dry Run] Would activate {Agent}, find its conversation and input box, then send:\n{Message}",
                adapter.Name, message);
            return true;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _configuration.AgentTimeoutSeconds)));
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
            if (!await _retryPolicy.ExecuteUntilTrueAsync(t => adapter.FindInputBoxAsync(t), retryOptions, token).ConfigureAwait(false)) return false;

            return await adapter.SendMessageAsync(message, token).ConfigureAwait(false);
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

    private async Task NotifyAsync(string title, string message, NotificationLevel level, CancellationToken cancellationToken)
    {
        if (!_configuration.NotificationsEnabled)
        {
            return;
        }

        try
        {
            await _notificationService.NotifyAsync(title, message, level, cancellationToken).ConfigureAwait(false);
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
