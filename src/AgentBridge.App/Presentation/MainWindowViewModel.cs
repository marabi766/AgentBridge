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
    private int _maximumIterations = 50;
    private int _agentTimeoutSeconds = 30;
    private int _retryCount = 3;
    private int _fileDebounceMilliseconds = 400;
    private bool _notificationsEnabled = true;
    private bool _startMinimized;

    public MainWindowViewModel(
        IOrchestratorService orchestrator,
        ISettingsService settingsService,
        ILogService logService,
        IAgentDiagnosticsService diagnosticsService)
    {
        _orchestrator = orchestrator;
        _settingsService = settingsService;
        _logService = logService;
        _diagnosticsService = diagnosticsService;
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
    public RelayCommand NavigateCommand { get; }
    public ObservableCollection<LogEntry> ActivityEntries { get; } = [];
    public Func<bool>? ConfirmStop { get; set; }

    public BridgeStatusView? Status { get => _status; private set { if (SetProperty(ref _status, value)) RaiseStatusProperties(); } }
    public string CurrentPage { get => _currentPage; set { if (SetProperty(ref _currentPage, value)) RaisePageProperties(); } }
    public string OperationMessage { get => _operationMessage; private set => SetProperty(ref _operationMessage, value); }
    public string ClaudeDiagnostics { get => _claudeDiagnostics; private set => SetProperty(ref _claudeDiagnostics, value); }
    public string CodexDiagnostics { get => _codexDiagnostics; private set => SetProperty(ref _codexDiagnostics, value); }

    public bool IsDashboard => CurrentPage == "Dashboard";
    public bool IsActivity => CurrentPage == "Activity";
    public bool IsDiagnostics => CurrentPage == "Diagnostics";
    public bool IsSettings => CurrentPage == "Settings";
    public bool HasError => !string.IsNullOrWhiteSpace(Status?.LastError);
    public bool CanStart => Status?.CurrentState is null or BridgeState.Idle or BridgeState.Stopped or BridgeState.Error;
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
    public int MaximumIterations { get => _maximumIterations; set => SetProperty(ref _maximumIterations, value); }
    public int AgentTimeoutSeconds { get => _agentTimeoutSeconds; set => SetProperty(ref _agentTimeoutSeconds, value); }
    public int RetryCount { get => _retryCount; set => SetProperty(ref _retryCount, value); }
    public int FileDebounceMilliseconds { get => _fileDebounceMilliseconds; set => SetProperty(ref _fileDebounceMilliseconds, value); }
    public bool NotificationsEnabled { get => _notificationsEnabled; set => SetProperty(ref _notificationsEnabled, value); }
    public bool StartMinimized { get => _startMinimized; set => SetProperty(ref _startMinimized, value); }

    public async Task InitializeAsync()
    {
        try
        {
            _configuration = await _settingsService.GetCurrentAsync(CancellationToken.None);
            LoadSettings(_configuration);
            if (string.IsNullOrWhiteSpace(_configuration.ProjectPath)) CurrentPage = "Settings";
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

    private async Task SaveSettingsAsync()
    {
        var candidate = _configuration with
        {
            ProjectPath = ProjectPath.Trim(),
            ClaudeReportFileName = ClaudeReportFileName.Trim(),
            CodexPromptFileName = CodexPromptFileName.Trim(),
            MaximumIterations = MaximumIterations,
            AgentTimeoutSeconds = AgentTimeoutSeconds,
            RetryCount = RetryCount,
            FileDebounceMilliseconds = FileDebounceMilliseconds,
            NotificationsEnabled = NotificationsEnabled,
            StartMinimized = StartMinimized,
            DryRun = true,
        };
        try
        {
            var result = await _settingsService.UpdateAsync(candidate, CancellationToken.None);
            if (!result.IsValid)
            {
                OperationMessage = "Settings were not saved: " + string.Join(" ", result.Errors);
                return;
            }

            _configuration = candidate;
            OperationMessage = "Settings saved atomically. Timing changes apply on the next run.";
            await RefreshAsync();
        }
        catch (Exception ex) { OperationMessage = $"Settings were not saved: {ex.Message}"; }
    }

    private void LoadSettings(BridgeConfiguration value)
    {
        ProjectPath = value.ProjectPath;
        ClaudeReportFileName = value.ClaudeReportFileName;
        CodexPromptFileName = value.CodexPromptFileName;
        MaximumIterations = value.MaximumIterations;
        AgentTimeoutSeconds = value.AgentTimeoutSeconds;
        RetryCount = value.RetryCount;
        FileDebounceMilliseconds = value.FileDebounceMilliseconds;
        NotificationsEnabled = value.NotificationsEnabled;
        StartMinimized = value.StartMinimized;
    }

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
    }

    private void RaisePageProperties()
    {
        OnPropertyChanged(nameof(IsDashboard));
        OnPropertyChanged(nameof(IsActivity));
        OnPropertyChanged(nameof(IsDiagnostics));
        OnPropertyChanged(nameof(IsSettings));
        if (IsActivity) LoadActivityCommand.Execute(null);
    }

    private static string FormatTimestamp(DateTimeOffset? value) => value is null
        ? "— not observed yet"
        : value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public void Dispose() => _orchestrator.StatusChanged -= OnStatusChanged;
}
