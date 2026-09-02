namespace AgentBridge.App;

using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using System.ComponentModel;
using System.IO;
using System.Windows;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DesktopNotificationService _tray;
    private readonly IOrchestratorService _orchestrator;
    private bool _allowClose;

    public MainWindow(
        MainWindowViewModel viewModel,
        DesktopNotificationService tray,
        IOrchestratorService orchestrator)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _tray = tray;
        _orchestrator = orchestrator;
        _viewModel.ConfirmStop = ConfirmStop;
        _viewModel.ConfirmReset = ConfirmReset;
        _viewModel.ConfirmLiveEnable = ConfirmLiveEnable;
        _viewModel.SelectProjectFolder = SelectProjectFolder;
        _viewModel.ThemeChanged = ThemeManager.Apply;
        _viewModel.NotificationsChanged = _tray.SetNotificationsEnabled;
        DataContext = viewModel;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;

        _tray.OpenRequested += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        _tray.StartRequested += (_, _) => Dispatcher.BeginInvoke(() =>
            (_viewModel.CanResume ? _viewModel.ResumeCommand : _viewModel.StartCommand).Execute(null));
        _tray.PauseRequested += (_, _) => Dispatcher.BeginInvoke(() => _viewModel.PauseCommand.Execute(null));
        _tray.StopRequested += (_, _) => Dispatcher.BeginInvoke(() => _viewModel.StopCommand.Execute(null));
        _tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(RequestExit);
        _orchestrator.StatusChanged += OnOrchestratorStatusChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
        _tray.SetNotificationsEnabled(_viewModel.NotificationsEnabled);
        if (_viewModel.Status is not null) _tray.UpdateStatus(_viewModel.Status);
        if (_viewModel.ShouldStartMinimized) Hide();
    }


    private bool ConfirmStop() => MessageBox.Show(
        this,
        "Stop the loop? The current step is abandoned, but iteration progress and file hashes are kept. Nothing is sent while stopping.",
        "Stop Agent Bridge",
        MessageBoxButton.OKCancel,
        MessageBoxImage.Warning,
        MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private bool ConfirmReset() => MessageBox.Show(
        this,
        "Resetting discards the current iteration counter, recorded protocol-file hashes, and retry progress. Protocol files, project settings, Git data, and logs are not changed.\n\nReset recovery state?",
        "Reset Agent Bridge state",
        MessageBoxButton.OKCancel,
        MessageBoxImage.Warning,
        MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private bool ConfirmLiveEnable() => MessageBox.Show(
        this,
        "Live mode can type into and invoke Send in both configured desktop conversations. Agent Bridge will refuse ambiguous targets and only report success after observing a cleared input plus an exact rendered copy of the message.\n\nVerify both conversation identifiers before continuing. Enable Live mode?",
        "Enable Live message delivery",
        MessageBoxButton.OKCancel,
        MessageBoxImage.Warning,
        MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private string? SelectProjectFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the project folder",
            InitialDirectory = Directory.Exists(_viewModel.ProjectPath) ? _viewModel.ProjectPath : null,
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void OnOrchestratorStatusChanged(object? sender, BridgeStatusView status) =>
        Dispatcher.BeginInvoke(() => _tray.UpdateStatus(status));

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == System.Windows.WindowState.Minimized) Hide();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void RequestExit()
    {
        if (_viewModel.CanStop && !ConfirmStop()) return;
        _allowClose = true;
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_viewModel.CanStop && !ConfirmStop())
        {
            e.Cancel = true;
            return;
        }
        _allowClose = true;
    }
}
