using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Cli;
using AgentBridge.Core.Orchestration;
using AgentBridge.Core.Retry;
using AgentBridge.Core.Templates;
using AgentBridge.Infrastructure.Agents;
using AgentBridge.Infrastructure.FileWatching;
using AgentBridge.Infrastructure.Git;
using AgentBridge.Infrastructure.Logging;
using AgentBridge.Infrastructure.Notifications;
using AgentBridge.Infrastructure.Persistence;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

var options = CommandLineOptions.Parse(args);

if (options.ParseError is not null)
{
    Console.Error.WriteLine(options.ParseError);
    Console.Error.WriteLine("Run 'agent-bridge --help' for the available options.");
    return ExitCodes.BadArguments;
}

if (options.ShowHelp)
{
    Console.WriteLine(CommandLineOptions.HelpText);
    return ExitCodes.Success;
}

HeadlessPaths.EnsureDirectoriesExist();
var settingsFilePath = options.SettingsFilePath ?? HeadlessPaths.DefaultSettingsFilePath;

// State lives beside the settings that produced it. Pinning it to one fixed
// path instead would mean two projects pointed at two settings files still
// resumed each other's iteration count.
var stateFilePath = Path.Combine(
    Path.GetDirectoryName(settingsFilePath) ?? HeadlessPaths.RootDirectory, "state.json");

// The settings file is the single source of truth for every part of the run,
// because the adapters and the orchestrator read it themselves rather than
// receiving it. So this invocation's options are folded in and written back
// before anything is constructed, which also means the next run with no
// arguments repeats this one.
var settingsWriter = new JsonConfigurationService(settingsFilePath, NullLogger<JsonConfigurationService>.Instance);
var configuration = options.ApplyTo(await settingsWriter.LoadAsync(CancellationToken.None));

if (string.IsNullOrWhiteSpace(configuration.ProjectPath))
{
    Console.Error.WriteLine("No project has been set. Run: agent-bridge --project <path>");
    return ExitCodes.BadArguments;
}

await settingsWriter.SaveAsync(configuration, CancellationToken.None);

var services = new ServiceCollection();
services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddProvider(new DailyFileLoggerProvider(new DailyFileLoggerOptions { LogsDirectory = HeadlessPaths.LogsDirectory }));
    logging.AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss ";
    });
    logging.SetMinimumLevel(LogLevel.Information);
});

services.AddSingleton(TimeProvider.System);
services.AddSingleton<IConfigurationService>(sp =>
    new JsonConfigurationService(settingsFilePath, sp.GetRequiredService<ILogger<JsonConfigurationService>>()));
services.AddSingleton<IStateStore>(sp =>
    new JsonStateStore(stateFilePath, sp.GetRequiredService<ILogger<JsonStateStore>>()));
services.AddSingleton<IGitService, GitService>();
services.AddSingleton<IProjectService, ProjectService>();
services.AddSingleton<INotificationService, NullNotificationService>();
services.AddSingleton<ITemplateEngine, PlaceholderTemplateEngine>();
services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();

// Only the command line adapters are registered. Nothing in this process reads
// a window, which is what lets the loop keep running while the desktop is
// locked — and it is also why the desktop-versus-CLI setting cannot matter here.
services.AddSingleton<IAgentAdapter>(sp => new ClaudeCliAdapter(
    sp.GetRequiredService<IConfigurationService>(),
    sp.GetRequiredService<ILogger<ClaudeCliAdapter>>()));
services.AddSingleton<IAgentAdapter>(sp => new CodexCliAdapter(
    sp.GetRequiredService<IConfigurationService>(),
    sp.GetRequiredService<ILogger<CodexCliAdapter>>()));
services.AddSingleton<IAgentAdapterProvider, DefaultAgentAdapterProvider>();

services.AddSingleton(new FileWatcherOptions
{
    DebounceMilliseconds = configuration.FileDebounceMilliseconds,
    StabilityCheckIntervalMilliseconds = configuration.FileStabilityCheckIntervalMilliseconds,
    RequiredConsecutiveStableChecks = configuration.FileStabilityRequiredConsecutiveChecks,
    ReadRetryCount = configuration.FileReadRetryCount,
    ReadRetryDelayMilliseconds = configuration.FileReadRetryDelayMilliseconds,
});
services.AddSingleton<IFileWatcherFactory, FileWatcherFactory>();
services.AddSingleton<AgentOrchestrator>();
services.AddSingleton<IOrchestratorService>(sp => sp.GetRequiredService<AgentOrchestrator>());

await using var provider = services.BuildServiceProvider();
var orchestrator = provider.GetRequiredService<IOrchestratorService>();
var adapters = provider.GetRequiredService<IAgentAdapterProvider>();

if (options.StatusOnly)
{
    await StatusReport.WriteAsync(configuration, settingsFilePath, stateFilePath, provider, CancellationToken.None);
    return ExitCodes.Success;
}

if (options.ResetState)
{
    await orchestrator.ResetStateAsync(CancellationToken.None);
    Console.WriteLine("Persisted state discarded.");
}

// Refuse a live run whose agents are not installed, here rather than three
// states later. The orchestrator would report it as a delivery failure, which is
// true but says nothing about what to fix.
if (!configuration.DryRun)
{
    var missing = new List<string>();
    foreach (var role in new[] { AgentRole.Claude, AgentRole.Codex })
    {
        var adapter = adapters.GetAdapter(role);
        if (!await adapter.IsApplicationRunningAsync(CancellationToken.None))
        {
            missing.Add(adapter.Name);
        }
    }

    if (missing.Count > 0)
    {
        Console.Error.WriteLine(
            $"Cannot start a live run: {string.Join(" and ", missing)} could not be found on PATH.");
        Console.Error.WriteLine("Run 'agent-bridge --status' for the resolved paths.");
        return ExitCodes.AgentUnavailable;
    }
}

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Handling it ourselves rather than letting the runtime kill the process: an
    // agent may be mid-run, and its child process has to be stopped with it.
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("Stopping. Press Ctrl+C again to abandon the run.");
    lifetime.Cancel();
};

var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var lastLine = string.Empty;

orchestrator.StatusChanged += (_, status) =>
{
    var line = StatusReport.Format(status);
    if (line != lastLine)
    {
        lastLine = line;
        Console.WriteLine(line);
    }

    if (status.CurrentState is BridgeState.Stopped or BridgeState.Error)
    {
        finished.TrySetResult();
    }
};

Console.WriteLine($"Project      : {configuration.ProjectPath}");
Console.WriteLine($"Mode         : {(configuration.DryRun ? "DRY RUN — nothing is sent" : "LIVE")}");
Console.WriteLine($"Protocol     : {configuration.ClaudeReportFileName} (Claude) / {configuration.CodexPromptFileName} (Codex)");
Console.WriteLine($"Settings     : {settingsFilePath}");
Console.WriteLine($"Logs         : {HeadlessPaths.LogsDirectory}");
Console.WriteLine();

if (options.StartPoint is { } startPoint)
{
    await orchestrator.StartAtAsync(startPoint, CancellationToken.None);
}
else
{
    await orchestrator.StartAsync(CancellationToken.None);
}

// Two ways out: the run ends on its own (iteration limit, or an error the
// orchestrator could not recover from), or the operator interrupts it.
var stoppedByOperator = false;
try
{
    await finished.Task.WaitAsync(lifetime.Token);
}
catch (OperationCanceledException)
{
    stoppedByOperator = true;
}

await orchestrator.StopAsync(CancellationToken.None);
(orchestrator as IDisposable)?.Dispose();

var final = await orchestrator.GetStatusAsync(CancellationToken.None);
Console.WriteLine();
Console.WriteLine(stoppedByOperator
    ? $"Stopped by request after {final.CurrentIteration} iteration(s)."
    : $"Finished in state {final.CurrentState} after {final.CurrentIteration} iteration(s).");

if (final.LastError is not null)
{
    Console.Error.WriteLine(final.LastError);
    return ExitCodes.RunFailed;
}

return ExitCodes.Success;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int BadArguments = 2;
    public const int AgentUnavailable = 3;
    public const int RunFailed = 1;
}
