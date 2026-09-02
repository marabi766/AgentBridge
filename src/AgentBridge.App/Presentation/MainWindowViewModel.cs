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
    private bool _startMinimized;
    private bool _darkTheme;
    private bool _dryRun = true;
    private int _setupStep = 1;
    private string _setupValidation = "No project validation has run yet.";

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

        StartCommand = new AsyncCommand(() => RunOperationAsync("Starting…", _orchestrator.StartAsync), () => CanStart);
        PauseCommand = new AsyncCommand(() => RunOperationAsync("Pausing…", _orchestrator.PauseAsync), () => CanPause);
        ResumeCommand = new AsyncCommand(() => RunOperationAsync("Resuming…", _orchestrator.ResumeAsync), () => CanResume);
        StopCommand = new AsyncCommand(StopSafelyAsync, () => CanStop);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        LoadActivityCommand = new AsyncCommand(LoadActivityAsync);
        TestClaudeCommand = new AsyncCommand(TestClaudeAsync);
        TestCodexCommand = new AsyncCommand(TestCodexAsync);
        SaveSettingsCommand = new AsyncCommand(SaveSettingsAsync);
        BrowseProjectCommand = new RelayCommand(_ => BrowseProject());
        SetupNextCommand = new AsyncCommand(SetupNextAsync);
        SetupBackCommand = new RelayCommand(_ => SetupStep--, _ => SetupStep > 1);
        ResetStateCommand = new AsyncCommand(ResetStateAsync, () => HasError);
        NavigateCommand = new RelayCommand(p => CurrentPage = p?.ToString() ?? "Dashboard");
    }

    public AsyncCommand StartCommand { get; }
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
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
    public RelayCommand NavigateCommand { get; }
    public ObservableCollection<LogEntry> ActivityEntries { get; } = [];
    public Func<bool>? ConfirmStop { get; set; }
    public Func<bool>? ConfirmReset { get; set; }
    public Func<bool>? ConfirmLiveEnable { get; set; }
    public Func<string?>? SelectProjectFolder { get; set; }
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
    public bool CanStop => Status?.CurrentState is not (null or BridgeState.Idle or BridgeState.Stopped);
    public string StateText => Status?.StatusText ?? "Loading";
    public string IterationText => Status is null ? "—" : $"{Status.CurrentIteration} / {Status.MaximumIterations}";
    public string ModeText => Status?.DryRun == false ? "LIVE" : "DRY RUN";
    public string ModeExplanation => Status?.DryRun == false
        ? "Real input is enabled. Delivery outcomes still require adapter verification."
        : "No keystrokes or clicks are sent to Claude or ChatGPT.";
    public string GeneratedText => Status is null ? "—" : Status.GeneratedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string LastActionText => Status?.LastAction ?? "Nothing has run yet.";
    public string LastErrorText => Status?.LastError ?? "None in this run";
    public string ClaudeStatusText => Status?.ClaudeStatus.ToString() ?? "Unknown";
    public string CodexStatusText => Status?.CodexStatus.ToString() ?? "Unknown";
    public string GitBranchText => Status?.GitBranch ?? "— not available";
    public string GitTreeText => Status?.GitWorkingTreeSummary ?? "— not available";
    public string ClaudeFileUpdateText => FormatTimestamp(Status?.LastClaudeReportUpdateUtc);
    public string CodexFileUpdateText => FormatTimestamp(Status?.LastCodexPromptUpdateUtc);

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
    public bool StartMinimized { get => _startMinimized; set => SetProperty(ref _startMinimized, value); }
    public bool DryRun { get => _dryRun; set => SetProperty(ref _dryRun, value); }
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
            await LoadActivityAsync();
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
        await RunOperationAsync("Resetting recovery state…", _orchestrator.ResetStateAsync);
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

    private async Task LoadActivityAsync()
    {
        try
        {
            var entries = await _logService.TailAsync(250, CancellationToken.None);
            ActivityEntries.Clear();
            foreach (var entry in entries.Reverse()) ActivityEntries.Add(entry);
        }
        catch (Exception ex) { OperationMessage = $"Could not read activity: {ex.Message}"; }
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
        StartMinimized = StartMinimized,
        DarkTheme = DarkTheme,
        DryRun = DryRun,
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
        StartMinimized = value.StartMinimized;
        DarkTheme = value.DarkTheme;
        DryRun = value.DryRun;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void OnStatusChanged(object? sender, BridgeStatusView status) =>
        _uiContext.Post(_ => Status = status, null);

    private void RaiseStatusProperties()
    {
        foreach (var name in new[] { nameof(HasError), nameof(CanStart), nameof(CanPause), nameof(CanResume), nameof(CanStop), nameof(StateText), nameof(IterationText), nameof(ModeText), nameof(ModeExplanation), nameof(GeneratedText), nameof(LastActionText), nameof(LastErrorText), nameof(ClaudeStatusText), nameof(CodexStatusText), nameof(GitBranchText), nameof(GitTreeText), nameof(ClaudeFileUpdateText), nameof(CodexFileUpdateText) })
            OnPropertyChanged(name);
        StartCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
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

    public void Dispose() => _orchestrator.StatusChanged -= OnStatusChanged;
}
