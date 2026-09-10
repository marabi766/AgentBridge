using System.Collections.ObjectModel;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using System.Globalization;

namespace AgentBridge.App;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IOrchestratorService _orchestrator;
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;
    private readonly IAgentDiagnosticsService _diagnosticsService;
    private readonly IProjectService _projectService;
    private readonly SynchronizationContext _uiContext;
    private readonly CancellationTokenSource _statusRefreshCts = new();
    private BridgeConfiguration _configuration = BridgeConfiguration.CreateDefault();
    private BridgeStatusView? _status;
    private string _currentPage = "Dashboard";
    private string _operationMessage = "Loading current status…";
    private string _claudeDiagnostics = "Not tested yet.";
    private string _codexDiagnostics = "Not tested yet.";
    private string _projectPath = string.Empty;
    private string _claudeReportFileName = "ClaudeResultReport.md";
    private string _codexPromptFileName = "CodexPrompt.md";
    private string _claudeConversationIdentifier = string.Empty;
    private string _codexConversationIdentifier = string.Empty;
    private int _maximumIterations = 50;
    private int _agentTimeoutSeconds = 30;
    private int _retryCount = 3;
    private int _fileDebounceMilliseconds = 400;
    private bool _notificationsEnabled = true;
    private bool _autoStart;
    private bool _startMinimized;
    private bool _darkTheme;
    private bool _dryRun = true;
    private bool _useClaudeCli;
    private bool _useCodexCli;
    private string _claudeCliExecutable = "claude";
    private string _claudeCliArguments = string.Empty;
    private int _claudeCliTimeoutSeconds = 3600;
    private string _codexCliExecutable = "codex";
    private string _codexCliArguments = string.Empty;
    private int _codexCliTimeoutSeconds = 1800;
    private BridgeStartPoint _selectedStartPoint = BridgeStartPoint.WaitForClaudeReport;
    private int _setupStep = 1;
    private string _setupValidation = "No project validation has run yet.";

    // Follows the log while the Activity page is open. Off by default would make
    // the page lie by omission during a live run: it would show the moment it was
    // opened and nothing after, with no sign that it had stopped keeping up.
    private bool _followActivity = true;

    // Where the Activity page has read up to. Negative means "start from the
    // end", which is how a fresh page begins without replaying the whole day.
    private long _activityPosition = -1;

    // The view holds more than one tail so a long run can be scrolled back
    // through, but not without bound: a run printing twenty thousand lines would
    // otherwise put every one of them in a grid.
    private const int MaximumActivityEntries = 2000;

    private int _agentLogLineLimit = 20_000;

    public MainWindowViewModel(
        IOrchestratorService orchestrator,
        ISettingsService settingsService,
        ILogService logService,
        IAgentDiagnosticsService diagnosticsService,
        IProjectService projectService)
    {
        _orchestrator = orchestrator;
        _settingsService = settingsService;
        _logService = logService;
        _diagnosticsService = diagnosticsService;
        _projectService = projectService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _orchestrator.StatusChanged += OnStatusChanged;

        StartCommand = new AsyncCommand(
            () => RunOperationAsync(
                "Starting from selected checkpoint…",
                token => _orchestrator.StartAtAsync(SelectedStartPoint, token)),
            () => CanStart);
        PauseCommand = new AsyncCommand(() => RunOperationAsync("Pausing…", _orchestrator.PauseAsync), () => CanPause);
        ResumeCommand = new AsyncCommand(() => RunOperationAsync("Resuming…", _orchestrator.ResumeAsync), () => CanResume);
        RetryClaudeCommand = new AsyncCommand(
            () => RunOperationAsync("Retrying Claude delivery…", _orchestrator.RetryClaudeDeliveryAsync),
            () => CanRetryClaude);
        RetryCodexCommand = new AsyncCommand(
            () => RunOperationAsync("Retrying Codex delivery…", _orchestrator.RetryCodexDeliveryAsync),
            () => CanRetryCodex);
        ContinueClaudeCommand = new AsyncCommand(
            () => RunOperationAsync("Asking Claude to continue…", _orchestrator.ContinueClaudeAsync),
            () => CanContinueClaude);
        ContinueCodexCommand = new AsyncCommand(
            () => RunOperationAsync("Asking Codex to continue…", _orchestrator.ContinueCodexAsync),
            () => CanContinueCodex);
        ContinueWaitingForClaudeCommand = new AsyncCommand(
            () => RunOperationAsync("Continuing to wait for Claude…", _orchestrator.ContinueWaitingForClaudeAsync),
            () => CanContinueWaitingForClaude);
        ContinueWaitingForCodexCommand = new AsyncCommand(
            () => RunOperationAsync("Continuing to wait for Codex…", _orchestrator.ContinueWaitingForCodexAsync),
            () => CanContinueWaitingForCodex);
        StopCommand = new AsyncCommand(StopSafelyAsync, () => CanStop);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        LoadActivityCommand = new AsyncCommand(LoadActivityAsync);
        TestClaudeCommand = new AsyncCommand(TestClaudeAsync);
        TestCodexCommand = new AsyncCommand(TestCodexAsync);
        SaveSettingsCommand = new AsyncCommand(SaveSettingsAsync);
        BrowseProjectCommand = new RelayCommand(_ => BrowseProject());
        SetupNextCommand = new AsyncCommand(SetupNextAsync);
        SetupBackCommand = new RelayCommand(_ => SetupStep--, _ => SetupStep > 1);
        ResetStateCommand = new AsyncCommand(ResetStateAsync, () => CanResetState);
        ExportActivityCommand = new AsyncCommand(ExportActivityAsync);
        ClearActivityCommand = new RelayCommand(_ => ClearActivityView());
        NavigateCommand = new RelayCommand(p => CurrentPage = p?.ToString() ?? "Dashboard");
    }

    public AsyncCommand StartCommand { get; }
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand RetryClaudeCommand { get; }
    public AsyncCommand RetryCodexCommand { get; }
    public AsyncCommand ContinueClaudeCommand { get; }
    public AsyncCommand ContinueCodexCommand { get; }
    public AsyncCommand ContinueWaitingForClaudeCommand { get; }
    public AsyncCommand ContinueWaitingForCodexCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand LoadActivityCommand { get; }
    public AsyncCommand TestClaudeCommand { get; }
    public AsyncCommand TestCodexCommand { get; }
    public AsyncCommand SaveSettingsCommand { get; }
    public RelayCommand BrowseProjectCommand { get; }
    public AsyncCommand SetupNextCommand { get; }
    public RelayCommand SetupBackCommand { get; }
    public AsyncCommand ResetStateCommand { get; }
    public AsyncCommand ExportActivityCommand { get; }
    public RelayCommand ClearActivityCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public ObservableCollection<LogEntry> ActivityEntries { get; } = [];
    public IReadOnlyList<BridgeStartPointOption> StartPointOptions { get; } =
    [
        new(
            BridgeStartPoint.WaitForClaudeReport,
            "Claude is working",
            "Wait for ClaudeResultReport.md, then hand the new report to Codex."),
        new(
            BridgeStartPoint.WaitForCodexPrompt,
            "Codex is working",
            "Wait for CodexPrompt.md, then hand the new prompt to Claude."),
    ];
    public Func<bool>? ConfirmStop { get; set; }
    public Func<bool>? ConfirmReset { get; set; }
    public Func<bool>? ConfirmLiveEnable { get; set; }
    public Func<string?>? SelectProjectFolder { get; set; }
    public Func<string, string?>? ChooseExportFile { get; set; }
    public Action<bool>? ThemeChanged { get; set; }
    public Action<bool>? NotificationsChanged { get; set; }

    public BridgeStatusView? Status { get => _status; private set { if (SetProperty(ref _status, value)) RaiseStatusProperties(); } }
    public string CurrentPage { get => _currentPage; set { if (SetProperty(ref _currentPage, value)) RaisePageProperties(); } }
    public string OperationMessage { get => _operationMessage; private set => SetProperty(ref _operationMessage, value); }
    public string ClaudeDiagnostics { get => _claudeDiagnostics; private set => SetProperty(ref _claudeDiagnostics, value); }
    public string CodexDiagnostics { get => _codexDiagnostics; private set => SetProperty(ref _codexDiagnostics, value); }

    public bool IsDashboard => CurrentPage == "Dashboard";
    public bool IsActivity => CurrentPage == "Activity";
    public bool IsDiagnostics => CurrentPage == "Diagnostics";
    public bool IsSettings => CurrentPage == "Settings";
    public bool IsSetup => CurrentPage == "Setup";
    public bool HasError => !string.IsNullOrWhiteSpace(Status?.LastError);
    public bool CanStart => Status?.CurrentState is null or BridgeState.Idle or BridgeState.Stopped;
    public bool CanPause => Status?.IsRunning == true;
    public bool CanResume => Status?.IsPaused == true;
    public bool CanRetryClaude => Status?.CurrentState == BridgeState.WaitingForClaudeReport
        && Status.CurrentIteration > 0;
    public bool CanRetryCodex => Status?.CurrentIteration > 0
        && (Status.CurrentState is BridgeState.WaitingForClaudeReport or BridgeState.WaitingForCodexPrompt
            || Status.CurrentState == BridgeState.Error
            && Status.LastError?.StartsWith("Failed to deliver instruction to Codex", StringComparison.Ordinal) == true);
    /// <summary>
    /// Continue is offered where an agent had a session worth carrying on: the
    /// state that waits for its file, and Error — which is usually exactly the
    /// run that stopped mid-way. Never while it is still working, because there
    /// is nothing to continue until it stops.
    /// </summary>
    public bool CanContinueClaude => Status?.CurrentIteration > 0
        && Status.ClaudeStatus != AgentStatus.Busy
        && Status.CurrentState is BridgeState.WaitingForClaudeReport or BridgeState.Error;
    public bool CanContinueCodex => Status?.CurrentIteration > 0
        && Status.CodexStatus != AgentStatus.Busy
        && Status.CurrentState is BridgeState.WaitingForCodexPrompt or BridgeState.Error;
    public bool CanContinueWaitingForClaude => Status?.CurrentState == BridgeState.Error
        && Status.LastError?.StartsWith("Failed to deliver instruction to Claude", StringComparison.Ordinal) == true;
    public bool CanContinueWaitingForCodex => Status?.CurrentState == BridgeState.Error
        && Status.LastError?.StartsWith("Failed to deliver instruction to Codex", StringComparison.Ordinal) == true;
    public bool CanStop => Status?.CurrentState is not (null or BridgeState.Idle or BridgeState.Stopped);

    /// <summary>
    /// Reset is available whenever the bridge is not running, not only after an
    /// error. Persisted state can strand a run without ever reaching Error: a
    /// protocol file whose hash is already recorded as consumed is skipped
    /// forever, so the bridge waits for a revision the other agent will never
    /// write. Gating recovery on Error left that case with no way out of the
    /// application at all — the file had to be deleted by hand.
    ///
    /// A run in flight is still excluded. Discarding the iteration counter and
    /// recorded hashes underneath a live orchestration would be a different and
    /// much worse problem than the one this solves.
    /// </summary>
    public bool CanResetState =>
        Status?.CurrentState is null or BridgeState.Idle or BridgeState.Stopped or BridgeState.Error;
    public BridgeStartPoint SelectedStartPoint
    {
        get => _selectedStartPoint;
        set
        {
            if (SetProperty(ref _selectedStartPoint, value))
            {
                OnPropertyChanged(nameof(SelectedStartPointDescription));
            }
        }
    }
    public string SelectedStartPointDescription => StartPointOptions
        .First(option => option.Value == SelectedStartPoint).Description;
    public string StateText => Status?.StatusText ?? "Loading";
    public string IterationText => Status is null ? "—" : $"{Status.CurrentIteration} / {Status.MaximumIterations}";
    public string ModeText => Status?.DryRun == false ? "LIVE" : "DRY RUN";
    public string ModeExplanation => Status?.DryRun == false
        ? "Real input is enabled. Delivery outcomes still require adapter verification."
        : "No keystrokes or clicks are sent to Claude or ChatGPT.";
    public string GeneratedText => Status is null ? "—" : Status.GeneratedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string LastActionText => Status?.LastAction ?? "Nothing has run yet.";
    public string LastErrorText => Status?.LastError ?? "None in this run";
    public string ClaudeStatusText => Status?.ClaudeStatus == AgentStatus.Unknown
        && Status.CurrentState == BridgeState.WaitingForClaudeReport
        ? "Busy (verifying)"
        : Status?.ClaudeStatus.ToString() ?? "Unknown";
    public string CodexStatusText => Status?.CodexStatus.ToString() ?? "Unknown";
    public string GitBranchText => Status?.GitBranch ?? "— not available";
    public string GitTreeText => Status?.GitWorkingTreeSummary ?? "— not available";
    public string ClaudeFileUpdateText => FormatTimestamp(Status?.LastClaudeReportUpdateUtc);
    public string CodexFileUpdateText => FormatTimestamp(Status?.LastCodexPromptUpdateUtc);
    public int CycleProgress => Status?.CurrentState switch
    {
        BridgeState.WaitingForClaudeReport => 1,
        BridgeState.ClaudeReportDetected => 2,
        BridgeState.WaitingForCodex => 3,
        BridgeState.CodexProcessing => 4,
        BridgeState.WaitingForCodexPrompt => 5,
        BridgeState.CodexPromptDetected => 6,
        BridgeState.WaitingForClaude => 7,
        BridgeState.ClaudeProcessing => 8,
        _ => 0,
    };
    public string CycleProgressText => $"Step {CycleProgress} of 8 · {StateText}";

    public string ProjectPath { get => _projectPath; set => SetProperty(ref _projectPath, value); }
    public string ClaudeReportFileName { get => _claudeReportFileName; set => SetProperty(ref _claudeReportFileName, value); }
    public string CodexPromptFileName { get => _codexPromptFileName; set => SetProperty(ref _codexPromptFileName, value); }
    public string ClaudeConversationIdentifier { get => _claudeConversationIdentifier; set => SetProperty(ref _claudeConversationIdentifier, value); }
    public string CodexConversationIdentifier { get => _codexConversationIdentifier; set => SetProperty(ref _codexConversationIdentifier, value); }
    public int MaximumIterations { get => _maximumIterations; set => SetProperty(ref _maximumIterations, value); }
    public int AgentTimeoutSeconds { get => _agentTimeoutSeconds; set => SetProperty(ref _agentTimeoutSeconds, value); }
    public int RetryCount { get => _retryCount; set => SetProperty(ref _retryCount, value); }
    public int FileDebounceMilliseconds { get => _fileDebounceMilliseconds; set => SetProperty(ref _fileDebounceMilliseconds, value); }
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (SetProperty(ref _notificationsEnabled, value)) NotificationsChanged?.Invoke(value);
        }
    }
    public bool AutoStart { get => _autoStart; set => SetProperty(ref _autoStart, value); }
    public bool StartMinimized { get => _startMinimized; set => SetProperty(ref _startMinimized, value); }
    public bool DryRun { get => _dryRun; set => SetProperty(ref _dryRun, value); }

    /// <summary>
    /// Whether the Activity page keeps reloading itself. Turning it off is what
    /// makes the list readable while scrolling back through it — a reload jumps
    /// to the newest entry, which fights anyone reading an older one.
    /// </summary>
    public bool FollowActivity { get => _followActivity; set => SetProperty(ref _followActivity, value); }

    /// <summary>
    /// Which way Claude is driven: its command line when true, the Claude
    /// desktop window when false. The command line keeps working while Windows
    /// is locked; reading a window does not.
    /// </summary>
    public bool UseClaudeCli
    {
        get => _useClaudeCli;
        set
        {
            if (SetProperty(ref _useClaudeCli, value))
            {
                OnPropertyChanged(nameof(UseClaudeDesktop));
            }
        }
    }

    /// <summary>
    /// The other side of the same choice, so the two radio buttons can each bind
    /// to a property instead of the view needing a converter to negate one.
    /// Setting it only acts when selected: a radio button also reports false as
    /// it loses selection, and acting on that would undo the choice just made.
    /// </summary>
    public bool UseClaudeDesktop
    {
        get => !_useClaudeCli;
        set
        {
            if (value)
            {
                UseClaudeCli = false;
            }
        }
    }

    /// <summary>
    /// Which way Codex is driven: its command line when true, the ChatGPT
    /// desktop window when false. Resolved per call by the adapter provider, so
    /// this takes effect on save rather than on the next restart.
    /// </summary>
    public bool UseCodexCli
    {
        get => _useCodexCli;
        set
        {
            if (SetProperty(ref _useCodexCli, value))
            {
                OnPropertyChanged(nameof(UseCodexDesktop));
            }
        }
    }

    /// <summary>
    /// The other side of the same choice, so the two radio buttons can each bind
    /// to a property instead of the view needing a converter to negate one.
    /// Setting it only acts when selected: a radio button also reports false as
    /// it loses selection, and acting on that would undo the choice just made.
    /// </summary>
    public bool UseCodexDesktop
    {
        get => !_useCodexCli;
        set
        {
            if (value)
            {
                UseCodexCli = false;
            }
        }
    }
    /// <summary>
    /// How each command line agent is invoked. Exposed rather than left to the
    /// settings file because the flags decide whether an unattended run can do
    /// its job at all: a permission mode that stops to ask has nobody to ask, and
    /// the run simply produces nothing. An operator who cannot change that from
    /// here cannot fix it without a text editor.
    /// </summary>
    public string ClaudeCliExecutable { get => _claudeCliExecutable; set => SetProperty(ref _claudeCliExecutable, value); }

    public string ClaudeCliArguments { get => _claudeCliArguments; set => SetProperty(ref _claudeCliArguments, value); }

    public int ClaudeCliTimeoutSeconds { get => _claudeCliTimeoutSeconds; set => SetProperty(ref _claudeCliTimeoutSeconds, value); }

    /// <summary>
    /// Lines of one agent run written to the log before the rest is kept for
    /// diagnostics only. Zero means no limit.
    /// </summary>
    public int AgentLogLineLimit { get => _agentLogLineLimit; set => SetProperty(ref _agentLogLineLimit, value); }

    public string CodexCliExecutable { get => _codexCliExecutable; set => SetProperty(ref _codexCliExecutable, value); }

    public string CodexCliArguments { get => _codexCliArguments; set => SetProperty(ref _codexCliArguments, value); }

    public int CodexCliTimeoutSeconds { get => _codexCliTimeoutSeconds; set => SetProperty(ref _codexCliTimeoutSeconds, value); }

    public bool DarkTheme
    {
        get => _darkTheme;
        set
        {
            if (SetProperty(ref _darkTheme, value)) ThemeChanged?.Invoke(value);
        }
    }
    public bool ShouldStartMinimized => StartMinimized && !string.IsNullOrWhiteSpace(ProjectPath);
    public int SetupStep
    {
        get => _setupStep;
        private set
        {
            var bounded = Math.Clamp(value, 1, 5);
            if (SetProperty(ref _setupStep, bounded))
            {
                OnPropertyChanged(nameof(SetupStepTitle));
                OnPropertyChanged(nameof(SetupNextText));
                OnPropertyChanged(nameof(IsSetupWelcome));
                OnPropertyChanged(nameof(IsSetupProject));
                OnPropertyChanged(nameof(IsSetupProtocol));
                OnPropertyChanged(nameof(IsSetupAgents));
                OnPropertyChanged(nameof(IsSetupReview));
                SetupBackCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string SetupValidation { get => _setupValidation; private set => SetProperty(ref _setupValidation, value); }
    public string SetupStepTitle => SetupStep switch
    {
        1 => "Welcome and safety",
        2 => "Select and validate the project",
        3 => "Confirm protocol files",
        4 => "Test desktop readiness",
        _ => "Review and finish",
    };
    public string SetupNextText => SetupStep == 5 ? "Finish setup" : "Next";
    public bool IsSetupWelcome => SetupStep == 1;
    public bool IsSetupProject => SetupStep == 2;
    public bool IsSetupProtocol => SetupStep == 3;
    public bool IsSetupAgents => SetupStep == 4;
    public bool IsSetupReview => SetupStep == 5;

    public async Task InitializeAsync()
    {
        try
        {
            _configuration = await _settingsService.GetCurrentAsync(CancellationToken.None);
            LoadSettings(_configuration);
            if (string.IsNullOrWhiteSpace(_configuration.ProjectPath)) CurrentPage = "Setup";
            await RefreshAsync();
            if (_configuration.AutoStart && CanStart)
            {
                await RunOperationAsync("Auto-starting…", _orchestrator.StartAsync);
            }
            await LoadActivityAsync();
            _ = RefreshStatusPeriodicallyAsync(_statusRefreshCts.Token);
            _ = FollowActivityPeriodicallyAsync(_statusRefreshCts.Token);
        }
        catch (Exception ex) { OperationMessage = $"Startup failed safely: {ex.Message}"; }
    }

    private async Task RunOperationAsync(string pendingText, Func<CancellationToken, Task> operation)
    {
        OperationMessage = pendingText;
        try
        {
            await operation(CancellationToken.None);
            await RefreshAsync();
            OperationMessage = "Status is current.";
        }
        catch (Exception ex) { OperationMessage = $"Operation failed: {ex.Message}"; }
    }

    private Task StopSafelyAsync() => ConfirmStop?.Invoke() == false
        ? Task.CompletedTask
        : RunOperationAsync("Stopping…", _orchestrator.StopAsync);

    private async Task ResetStateAsync()
    {
        if (ConfirmReset?.Invoke() != true) return;
        await RunOperationAsync("Resetting bridge state…", _orchestrator.ResetStateAsync);
    }

    private void BrowseProject()
    {
        var selected = SelectProjectFolder?.Invoke();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            ProjectPath = selected;
            SetupValidation = "Folder selected. Choose Next to validate it without changing any files.";
        }
    }

    private async Task SetupNextAsync()
    {
        if (SetupStep == 2)
        {
            var candidate = BuildConfiguration();
            var validation = await _projectService.ValidateProjectAsync(candidate, CancellationToken.None);
            var facts = new[]
            {
                validation.PathExists ? "Folder exists and is readable." : "Folder is unavailable.",
                validation.IsGitRepository ? "Git repository detected." : "Git repository not detected; Git status will be unavailable.",
            };
            SetupValidation = string.Join(" ", facts.Concat(validation.Errors).Concat(validation.Warnings));
            if (!validation.IsValid) return;
        }

        if (SetupStep == 3)
        {
            var validation = await _settingsService.ValidateAsync(BuildConfiguration(), CancellationToken.None);
            if (!validation.IsValid)
            {
                SetupValidation = string.Join(" ", validation.Errors);
                return;
            }
            SetupValidation = "Protocol filenames are safe project-root filenames.";
        }

        if (SetupStep == 4)
        {
            await TestClaudeAsync();
            await TestCodexAsync();
            SetupValidation = "Readiness probes completed. They do not type, click, or send. Configure exact conversation titles in Settings before enabling Live mode.";
        }

        if (SetupStep == 5)
        {
            if (await SaveSettingsCoreAsync())
            {
                CurrentPage = "Dashboard";
                SetupStep = 1;
            }
            return;
        }

        SetupStep++;
    }

    private async Task RefreshAsync()
    {
        try
        {
            Status = await _orchestrator.GetStatusAsync(CancellationToken.None);
            OperationMessage = "Status is current.";
        }
        catch (Exception ex) { OperationMessage = $"Could not refresh status: {ex.Message}"; }
    }

    private async Task RefreshStatusPeriodicallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var status = await _orchestrator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    _uiContext.Post(_ =>
                    {
                        Status = status;
                        OperationMessage = "Status is current.";
                    }, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _uiContext.Post(_ => OperationMessage = $"Could not refresh status: {ex.Message}", null);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    /// <summary>
    /// Replaces the list with a fresh tail. This is Refresh — the deliberate
    /// "show me what is there", as against the incremental follow below.
    /// </summary>
    private async Task LoadActivityAsync()
    {
        try
        {
            var tail = await _logService.ReadSinceAsync(-1, 250, CancellationToken.None);
            _activityPosition = tail.Position;
            ActivityEntries.Clear();
            foreach (var entry in tail.Entries.Reverse()) ActivityEntries.Add(entry);
        }
        catch (Exception ex) { OperationMessage = $"Could not read activity: {ex.Message}"; }
    }

    /// <summary>
    /// Reloads the Activity list while that page is open and following is on.
    /// Runs on its own cadence rather than with the status poll: the log is read
    /// from disk and there is no reason to pay for that on the other pages.
    /// </summary>
    private async Task FollowActivityPeriodicallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsActivity || !FollowActivity)
                {
                    continue;
                }

                try
                {
                    var tail = await _logService
                        .ReadSinceAsync(_activityPosition, 250, cancellationToken)
                        .ConfigureAwait(false);

                    if (tail.Entries.Count == 0 && !tail.Restarted)
                    {
                        _activityPosition = tail.Position;
                        continue;
                    }

                    _uiContext.Post(_ =>
                    {
                        // A new day's file is a different stretch of time. Showing
                        // it above yesterday's entries would read as one run.
                        if (tail.Restarted)
                        {
                            ActivityEntries.Clear();
                        }

                        // Newest first, so each arrival goes to the top and the
                        // entries already there are not touched — which is what
                        // leaves a reader's scroll position and selection alone.
                        foreach (var entry in tail.Entries)
                        {
                            ActivityEntries.Insert(0, entry);
                        }

                        while (ActivityEntries.Count > MaximumActivityEntries)
                        {
                            ActivityEntries.RemoveAt(ActivityEntries.Count - 1);
                        }
                    }, null);

                    _activityPosition = tail.Position;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _uiContext.Post(_ => OperationMessage = $"Could not follow activity: {ex.Message}", null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ExportActivityAsync()
    {
        var suggested = $"agent-bridge-log-{DateTime.Now:yyyy-MM-dd-HHmm}.txt";
        var destination = ChooseExportFile?.Invoke(suggested);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        try
        {
            var lines = await _logService.ExportAsync(destination, CancellationToken.None);
            OperationMessage = $"Exported {lines} log lines to {destination}.";
        }
        catch (Exception ex) { OperationMessage = $"Could not export the log: {ex.Message}"; }
    }

    /// <summary>
    /// Empties what the page is showing and carries on from here, so what
    /// appears next is only what happens next. The log files are untouched —
    /// this is a clean view to watch the next step in, not a way to destroy the
    /// record of the last one, and Refresh brings that record straight back.
    ///
    /// Following used to have to be switched off here: the page re-read the whole
    /// file, so the next tick refilled the list within seconds and the button
    /// looked broken. Reading forward from where it left off means nothing old
    /// can come back on its own, so following stays on and the cleared view fills
    /// with the next step as it happens.
    /// </summary>
    private void ClearActivityView()
    {
        ActivityEntries.Clear();
        OperationMessage = FollowActivity
            ? "Activity view cleared. Only what happens from now on will appear; Refresh brings the log back."
            : "Activity view cleared. The log files are unchanged — Refresh brings them back.";
    }

    private async Task TestClaudeAsync()
    {
        ClaudeDiagnostics = "Testing…";
        try
        {
            var reachable = await _orchestrator.TestClaudeConnectionAsync(CancellationToken.None);
            var details = await _diagnosticsService.GetClaudeDiagnosticsAsync(CancellationToken.None);
            ClaudeDiagnostics = $"Connection: {(reachable ? "ready" : "not ready")}\n{details}";
            await RefreshAsync();
        }
        catch (Exception ex) { ClaudeDiagnostics = $"Test failed: {ex.Message}"; }
    }

    private async Task TestCodexAsync()
    {
        CodexDiagnostics = "Testing…";
        try
        {
            var reachable = await _orchestrator.TestCodexConnectionAsync(CancellationToken.None);
            var details = await _diagnosticsService.GetCodexDiagnosticsAsync(CancellationToken.None);
            CodexDiagnostics = $"Connection: {(reachable ? "ready" : "not ready")}\n{details}";
            await RefreshAsync();
        }
        catch (Exception ex) { CodexDiagnostics = $"Test failed: {ex.Message}"; }
    }

    private async Task SaveSettingsAsync() => _ = await SaveSettingsCoreAsync();

    private async Task<bool> SaveSettingsCoreAsync()
    {
        if (CanStop)
        {
            OperationMessage = "Stop the bridge before changing settings; the active run keeps its current safety mode.";
            return false;
        }

        var candidate = BuildConfiguration();
        try
        {
            var validation = await _settingsService.ValidateAsync(candidate, CancellationToken.None);
            if (!validation.IsValid)
            {
                OperationMessage = "Settings were not saved: " + string.Join(" ", validation.Errors);
                return false;
            }

            if (_configuration.DryRun && !candidate.DryRun && !(ConfirmLiveEnable?.Invoke() ?? false))
            {
                DryRun = true;
                OperationMessage = "Live mode was not enabled; Dry Run remains selected.";
                return false;
            }

            var result = await _settingsService.UpdateAsync(candidate, CancellationToken.None);

            _configuration = candidate;
            OperationMessage = "Settings saved atomically. Process-path and watcher-timing changes apply after restarting Agent Bridge.";
            await RefreshAsync();
            return true;
        }
        catch (Exception ex)
        {
            OperationMessage = $"Settings were not saved: {ex.Message}";
            return false;
        }
    }

    private BridgeConfiguration BuildConfiguration() => _configuration with
    {
        ProjectPath = ProjectPath.Trim(),
        ClaudeReportFileName = ClaudeReportFileName.Trim(),
        CodexPromptFileName = CodexPromptFileName.Trim(),
        ClaudeConversationIdentifier = NullIfWhiteSpace(ClaudeConversationIdentifier),
        CodexConversationIdentifier = NullIfWhiteSpace(CodexConversationIdentifier),
        MaximumIterations = MaximumIterations,
        AgentTimeoutSeconds = AgentTimeoutSeconds,
        RetryCount = RetryCount,
        FileDebounceMilliseconds = FileDebounceMilliseconds,
        NotificationsEnabled = NotificationsEnabled,
        AutoStart = AutoStart,
        StartMinimized = StartMinimized,
        DarkTheme = DarkTheme,
        DryRun = DryRun,
        UseClaudeCli = UseClaudeCli,
        UseCodexCli = UseCodexCli,
        ClaudeCliExecutable = ClaudeCliExecutable,
        ClaudeCliArguments = ClaudeCliArguments,
        ClaudeCliTimeoutSeconds = ClaudeCliTimeoutSeconds,
        AgentLogLineLimit = AgentLogLineLimit,
        CodexCliExecutable = CodexCliExecutable,
        CodexCliArguments = CodexCliArguments,
        CodexCliTimeoutSeconds = CodexCliTimeoutSeconds,
    };

    private void LoadSettings(BridgeConfiguration value)
    {
        ProjectPath = value.ProjectPath;
        ClaudeReportFileName = value.ClaudeReportFileName;
        CodexPromptFileName = value.CodexPromptFileName;
        ClaudeConversationIdentifier = value.ClaudeConversationIdentifier ?? string.Empty;
        CodexConversationIdentifier = value.CodexConversationIdentifier ?? string.Empty;
        MaximumIterations = value.MaximumIterations;
        AgentTimeoutSeconds = value.AgentTimeoutSeconds;
        RetryCount = value.RetryCount;
        FileDebounceMilliseconds = value.FileDebounceMilliseconds;
        NotificationsEnabled = value.NotificationsEnabled;
        AutoStart = value.AutoStart;
        StartMinimized = value.StartMinimized;
        DarkTheme = value.DarkTheme;
        DryRun = value.DryRun;
        UseClaudeCli = value.UseClaudeCli;
        UseCodexCli = value.UseCodexCli;
        ClaudeCliExecutable = value.ClaudeCliExecutable;
        ClaudeCliArguments = value.ClaudeCliArguments;
        ClaudeCliTimeoutSeconds = value.ClaudeCliTimeoutSeconds;
        AgentLogLineLimit = value.AgentLogLineLimit;
        CodexCliExecutable = value.CodexCliExecutable;
        CodexCliArguments = value.CodexCliArguments;
        CodexCliTimeoutSeconds = value.CodexCliTimeoutSeconds;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void OnStatusChanged(object? sender, BridgeStatusView status) =>
        _uiContext.Post(_ => Status = status, null);

    private void RaiseStatusProperties()
    {
        foreach (var name in new[] { nameof(HasError), nameof(CanStart), nameof(CanPause), nameof(CanResume), nameof(CanRetryClaude), nameof(CanRetryCodex), nameof(CanContinueClaude), nameof(CanContinueCodex), nameof(CanContinueWaitingForClaude), nameof(CanContinueWaitingForCodex), nameof(CanStop), nameof(CanResetState), nameof(StateText), nameof(IterationText), nameof(ModeText), nameof(ModeExplanation), nameof(GeneratedText), nameof(LastActionText), nameof(LastErrorText), nameof(ClaudeStatusText), nameof(CodexStatusText), nameof(GitBranchText), nameof(GitTreeText), nameof(ClaudeFileUpdateText), nameof(CodexFileUpdateText), nameof(CycleProgress), nameof(CycleProgressText) })
            OnPropertyChanged(name);
        StartCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        RetryClaudeCommand.RaiseCanExecuteChanged();
        RetryCodexCommand.RaiseCanExecuteChanged();
        ContinueClaudeCommand.RaiseCanExecuteChanged();
        ContinueCodexCommand.RaiseCanExecuteChanged();
        ContinueWaitingForClaudeCommand.RaiseCanExecuteChanged();
        ContinueWaitingForCodexCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ResetStateCommand.RaiseCanExecuteChanged();
    }

    private void RaisePageProperties()
    {
        OnPropertyChanged(nameof(IsDashboard));
        OnPropertyChanged(nameof(IsActivity));
        OnPropertyChanged(nameof(IsDiagnostics));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsSetup));
        if (IsActivity) LoadActivityCommand.Execute(null);
    }

    private static string FormatTimestamp(DateTimeOffset? value) => value is null
        ? "— not observed yet"
        : value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _statusRefreshCts.Cancel();
        _statusRefreshCts.Dispose();
        _orchestrator.StatusChanged -= OnStatusChanged;
    }
}

public sealed record BridgeStartPointOption(
    BridgeStartPoint Value,
    string Label,
    string Description);
