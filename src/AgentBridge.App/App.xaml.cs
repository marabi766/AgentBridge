using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Core.Orchestration;
using AgentBridge.Core.Retry;
using AgentBridge.Core.Templates;
using AgentBridge.Fakes;
using AgentBridge.Infrastructure.FileWatching;
using AgentBridge.Infrastructure.Git;
using AgentBridge.Infrastructure.Logging;
using AgentBridge.Infrastructure.Paths;
using AgentBridge.Infrastructure.Persistence;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Windows;

namespace AgentBridge.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private SingleInstanceCoordinator? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new SingleInstanceCoordinator(() => Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window)
            {
                window.ShowFromTray();
            }
        }));
        if (!_singleInstance.IsPrimary)
        {
            Shutdown();
            return;
        }

        AppPaths.EnsureDirectoriesExist();

        var builder = Host.CreateApplicationBuilder(e.Args);
        ConfigureServices(builder);
        _host = builder.Build();
        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                await _host.Services.GetRequiredService<IOrchestratorService>().StopAsync(CancellationToken.None);
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                _host.Dispose();
            }
        }

        _singleInstance?.Dispose();

        base.OnExit(e);
    }

    private static void ConfigureServices(HostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new DailyFileLoggerProvider(new DailyFileLoggerOptions { LogsDirectory = AppPaths.LogsDirectory }));
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IConfigurationService>(sp =>
            new JsonConfigurationService(AppPaths.SettingsFilePath, sp.GetRequiredService<ILogger<JsonConfigurationService>>()));
        builder.Services.AddSingleton<IStateStore>(sp =>
            new JsonStateStore(AppPaths.StateFilePath, sp.GetRequiredService<ILogger<JsonStateStore>>()));
        builder.Services.AddSingleton<IGitService, GitService>();
        builder.Services.AddSingleton<IProjectService, ProjectService>();
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<DesktopNotificationService>();
        builder.Services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<DesktopNotificationService>());
        builder.Services.AddSingleton<ITemplateEngine, PlaceholderTemplateEngine>();
        builder.Services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
        builder.Services.AddSingleton<ILogService>(_ => new FileLogService(AppPaths.LogsDirectory));

        // Live delivery remains unavailable until a receipt-verifying adapter exists.
        builder.Services.AddSingleton<IAgentAdapter, FakeClaudeAdapter>();
        builder.Services.AddSingleton<IAgentAdapter, FakeCodexAdapter>();
        builder.Services.AddSingleton<IAgentAdapterProvider, DefaultAgentAdapterProvider>();
        builder.Services.AddSingleton<IAgentDiagnosticsService, AgentDiagnosticsService>();

        var bootstrapConfig = new JsonConfigurationService(AppPaths.SettingsFilePath, NullLogger<JsonConfigurationService>.Instance)
            .LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        builder.Services.AddSingleton(new FileWatcherOptions
        {
            DebounceMilliseconds = bootstrapConfig.FileDebounceMilliseconds,
            StabilityCheckIntervalMilliseconds = bootstrapConfig.FileStabilityCheckIntervalMilliseconds,
            RequiredConsecutiveStableChecks = bootstrapConfig.FileStabilityRequiredConsecutiveChecks,
            ReadRetryCount = bootstrapConfig.FileReadRetryCount,
            ReadRetryDelayMilliseconds = bootstrapConfig.FileReadRetryDelayMilliseconds,
        });
        builder.Services.AddSingleton<IFileWatcherFactory, FileWatcherFactory>();

        builder.Services.AddSingleton<AgentOrchestrator>();
        builder.Services.AddSingleton<IOrchestratorService>(sp => sp.GetRequiredService<AgentOrchestrator>());
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
    }
}
