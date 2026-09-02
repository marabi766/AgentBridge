namespace AgentBridge.App;

using System.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.ConfirmStop = ConfirmStop;
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }


    private bool ConfirmStop() => MessageBox.Show(
        this,
        "Stop the loop? The current step is abandoned, but iteration progress and file hashes are kept. Nothing is sent while stopping.",
        "Stop Agent Bridge",
        MessageBoxButton.OKCancel,
        MessageBoxImage.Warning,
        MessageBoxResult.Cancel) == MessageBoxResult.OK;
}
