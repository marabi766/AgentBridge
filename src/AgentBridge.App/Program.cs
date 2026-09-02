using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Core.Orchestration;
using AgentBridge.Core.Retry;
using AgentBridge.Core.Templates;
using AgentBridge.Fakes;
using AgentBridge.Infrastructure.FileWatching;
using AgentBridge.Infrastructure.Git;
using AgentBridge.Infrastructure.Logging;
using AgentBridge.Infrastructure.Notifications;
using AgentBridge.Infrastructure.Paths;
using AgentBridge.Infrastructure.Persistence;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// -----------------------------------------------------------------------------
// AgentBridge.App — MINIMAL BACKEND HOST, NOT THE PRODUCT UI.
//
// Per the current project phase, the real dashboard/tray/settings/wizard UI is
// intentionally deferred (see FUTURE_UI.md and ARCHITECTURE.md). This console
// shell exists only so the fully independent backend (state machine, file
// watcher, orchestrator, persistence, retry/timeout, fake agent adapters) can be
// built, run, and manually poked without a UI. It talks to IOrchestratorService
// exactly the way a future WPF dashboard will — no orchestration logic lives here.
// -----------------------------------------------------------------------------

AppPaths.EnsureDirectoriesExist();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
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
builder.Services.AddSingleton<INotificationService, NullNotificationService>();
builder.Services.AddSingleton<ITemplateEngine, PlaceholderTemplateEngine>();
builder.Services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
builder.Services.AddSingleton<ILogService>(_ => new FileLogService(AppPaths.LogsDirectory));

// Real Claude Desktop / ChatGPT Desktop UI Automation adapters are not wired here yet
// (see UI_AUTOMATION.md) — the backend is proven end-to-end with fakes instead.
builder.Services.AddSingleton<IAgentAdapter, FakeClaudeAdapter>();
builder.Services.AddSingleton<IAgentAdapter, FakeCodexAdapter>();
builder.Services.AddSingleton<IAgentAdapterProvider, DefaultAgentAdapterProvider>();
builder.Services.AddSingleton<IAgentDiagnosticsService, AgentDiagnosticsService>();

// FileWatcherOptions are resolved once at boot from whatever is currently on disk.
// A live Settings UI in a later phase can push updated options before Start.
var bootstrapConfig = new JsonConfigurationService(AppPaths.SettingsFilePath, NullLogger<JsonConfigurationService>.Instance)
    .LoadAsync(CancellationToken.None)
    .GetAwaiter()
    .GetResult();

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

using var host = builder.Build();

var orchestrator = host.Services.GetRequiredService<IOrchestratorService>();

orchestrator.StatusChanged += (_, status) =>
{
    Console.WriteLine(
        $"[{status.GeneratedAtUtc:HH:mm:ss}] {status.StatusText}  iter={status.CurrentIteration}/{status.MaximumIterations}  " +
        $"dryRun={status.DryRun}  lastAction={status.LastAction}");
};

Console.WriteLine("Agent Bridge — backend diagnostic shell (the real dashboard is a later phase).");
Console.WriteLine($"Config file: {AppPaths.SettingsFilePath}");
Console.WriteLine($"State file:  {AppPaths.StateFilePath}");
Console.WriteLine($"Logs:        {AppPaths.LogsDirectory}");
Console.WriteLine("Commands: start | pause | resume | stop | status | reset | testclaude | testcodex | quit");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.IsCancellationRequested)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null)
    {
        break;
    }

    try
    {
        switch (line.Trim().ToLowerInvariant())
        {
            case "start":
                await orchestrator.StartAsync(cts.Token);
                break;
            case "pause":
                await orchestrator.PauseAsync(cts.Token);
                break;
            case "resume":
                await orchestrator.ResumeAsync(cts.Token);
                break;
            case "stop":
                await orchestrator.StopAsync(cts.Token);
                break;
            case "reset":
                await orchestrator.ResetStateAsync(cts.Token);
                break;
            case "status":
                var s = await orchestrator.GetStatusAsync(cts.Token);
                Console.WriteLine(
                    $"State={s.CurrentState} Text='{s.StatusText}' Iter={s.CurrentIteration}/{s.MaximumIterations} " +
                    $"Claude={s.ClaudeStatus} Codex={s.CodexStatus} Branch={s.GitBranch} Git={s.GitWorkingTreeSummary} " +
                    $"DryRun={s.DryRun} LastAction={s.LastAction} LastError={s.LastError}");
                break;
            case "testclaude":
                Console.WriteLine($"Claude reachable: {await orchestrator.TestClaudeConnectionAsync(cts.Token)}");
                break;
            case "testcodex":
                Console.WriteLine($"Codex reachable: {await orchestrator.TestCodexConnectionAsync(cts.Token)}");
                break;
            case "quit" or "exit":
                cts.Cancel();
                break;
            case "":
                break;
            default:
                Console.WriteLine("Unknown command. Try: start | pause | resume | stop | status | reset | testclaude | testcodex | quit");
                break;
        }
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Command failed: {ex.Message}");
    }
}

await orchestrator.StopAsync(CancellationToken.None);
