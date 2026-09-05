using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Core.Orchestration;
using AgentBridge.Core.Retry;
using AgentBridge.Core.Templates;
using AgentBridge.Infrastructure.Agents;
using AgentBridge.Infrastructure.FileWatching;
using AgentBridge.Infrastructure.Git;
using AgentBridge.Infrastructure.Logging;
using AgentBridge.Infrastructure.Paths;
using AgentBridge.Infrastructure.Persistence;
using AgentBridge.Infrastructure.Services;
using AgentBridge.UIAutomation.Adapters;
using AgentBridge.UIAutomation.Locators;
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                // WPF is already tearing down its Dispatcher here. Awaiting from an
                // async void OnExit can strand the process after the window closes,
                // leaving the single-instance mutex held without a usable UI.
                _host.Services.GetRequiredService<IOrchestratorService>()
                    .StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
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

        var bootstrapConfig = new JsonConfigurationService(AppPaths.SettingsFilePath, NullLogger<JsonConfigurationService>.Instance)
            .LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        builder.Services.AddSingleton<IConversationLocator, SemanticConversationLocator>();
        builder.Services.AddSingleton<IInputLocator, SemanticInputLocator>();
        builder.Services.AddSingleton<IMessageSender, VerifiedMessageSender>();
        builder.Services.AddSingleton<IAgentAdapter>(sp => new ClaudeDesktopAdapter(
            bootstrapConfig.ClaudeProcessName,
            bootstrapConfig.ClaudeExecutablePath,
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetRequiredService<IConversationLocator>(),
            sp.GetRequiredService<IInputLocator>(),
            sp.GetRequiredService<IMessageSender>(),
            sp.GetRequiredService<ILogger<ClaudeDesktopAdapter>>()));
        // Codex is reachable two ways and only one of them is registered, because
        // the provider resolves an adapter by role and two claimants for the same
        // role would make which one runs an accident of ordering.
        if (bootstrapConfig.UseCodexCli)
        {
            builder.Services.AddSingleton<IAgentAdapter>(sp => new CodexCliAdapter(
                sp.GetRequiredService<IConfigurationService>(),
                sp.GetRequiredService<ILogger<CodexCliAdapter>>()));
        }
        else
        {
            builder.Services.AddSingleton<IAgentAdapter>(sp => new ChatGptDesktopAdapter(
                bootstrapConfig.ChatGptProcessName,
                bootstrapConfig.ChatGptExecutablePath,
                sp.GetRequiredService<IConfigurationService>(),
                sp.GetRequiredService<IConversationLocator>(),
                sp.GetRequiredService<IInputLocator>(),
                sp.GetRequiredService<IMessageSender>(),
                sp.GetRequiredService<ILogger<ChatGptDesktopAdapter>>()));
        }
        builder.Services.AddSingleton<IAgentAdapterProvider, DefaultAgentAdapterProvider>();
        builder.Services.AddSingleton<IAgentDiagnosticsService, AgentDiagnosticsService>();

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
