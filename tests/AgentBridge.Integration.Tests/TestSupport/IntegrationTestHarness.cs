using System.Diagnostics;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Orchestration;
using AgentBridge.Core.Retry;
using AgentBridge.Core.Templates;
using AgentBridge.Fakes;
using AgentBridge.Infrastructure.FileWatching;
using AgentBridge.Infrastructure.Git;
using AgentBridge.Infrastructure.Notifications;
using AgentBridge.Infrastructure.Persistence;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Integration.Tests.TestSupport;

/// <summary>
/// Wires a REAL AgentOrchestrator against REAL infrastructure (file watcher, JSON
/// state store, git service, project/settings services, template engine, retry
/// policy) with only the two GUI-facing agent adapters faked out — this is the
/// "full automated simulation" the project spec calls for: the entire backend
/// exercised end-to-end through actual file writes on disk, with nothing mocked
/// except the parts that would otherwise require real Claude Desktop / ChatGPT
/// Desktop windows.
/// </summary>
public sealed class IntegrationTestHarness : IDisposable
{
    public string ProjectDir { get; }

    public string AppDataDir { get; }

    public BridgeConfiguration Configuration { get; }

    public FakeClaudeAdapter ClaudeAdapter { get; } = new();

    public FakeCodexAdapter CodexAdapter { get; } = new();

    public AgentOrchestrator Orchestrator { get; }

    public IProjectService ProjectService { get; }

    private readonly IStateStore _stateStore;

    public IntegrationTestHarness(int maximumIterations = 50, bool dryRun = false, bool initGitRepo = true)
    {
        ProjectDir = Path.Combine(Path.GetTempPath(), "abint-proj-" + Guid.NewGuid().ToString("N"));
        AppDataDir = Path.Combine(Path.GetTempPath(), "abint-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ProjectDir);
        Directory.CreateDirectory(AppDataDir);

        if (initGitRepo)
        {
            InitGitRepo(ProjectDir);
        }

        Configuration = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = ProjectDir,
            DryRun = dryRun,
            MaximumIterations = maximumIterations,
            AgentTimeoutSeconds = 5,
            RetryCount = 1,
            RetryInitialDelayMilliseconds = 20,
            RetryMaxDelayMilliseconds = 100,
            FileDebounceMilliseconds = 80,
            FileStabilityCheckIntervalMilliseconds = 50,
            FileStabilityRequiredConsecutiveChecks = 2,
        };

        var configService = new StaticConfigurationService(Configuration);
        var gitService = new GitService(NullLogger<GitService>.Instance);
        ProjectService = new ProjectService(gitService);
        _stateStore = new JsonStateStore(Path.Combine(AppDataDir, "AgentBridgeState.json"), NullLogger<JsonStateStore>.Instance);
        var watcherFactory = new FileWatcherFactory(
            new FileWatcherOptions
            {
                DebounceMilliseconds = Configuration.FileDebounceMilliseconds,
                StabilityCheckIntervalMilliseconds = Configuration.FileStabilityCheckIntervalMilliseconds,
                RequiredConsecutiveStableChecks = Configuration.FileStabilityRequiredConsecutiveChecks,
                ReadRetryCount = Configuration.FileReadRetryCount,
                ReadRetryDelayMilliseconds = Configuration.FileReadRetryDelayMilliseconds,
            },
            NullLoggerFactory.Instance);

        Orchestrator = new AgentOrchestrator(
            _stateStore,
            configService,
            watcherFactory,
            new SimpleAgentAdapterProvider(ClaudeAdapter, CodexAdapter),
            gitService,
            new PlaceholderTemplateEngine(),
            new ExponentialBackoffRetryPolicy(TimeProvider.System, NullLogger<ExponentialBackoffRetryPolicy>.Instance),
            new NullNotificationService(NullLogger<NullNotificationService>.Instance),
            ProjectService,
            TimeProvider.System,
            NullLogger<AgentOrchestrator>.Instance);
    }

    public string ClaudeReportPath => ProjectService.GetClaudeReportFilePath(Configuration);

    public string CodexPromptPath => ProjectService.GetCodexPromptFilePath(Configuration);

    public Task StartAsync() => Orchestrator.StartAsync(CancellationToken.None);

    public Task WriteClaudeReportAsync(string content) => File.WriteAllTextAsync(ClaudeReportPath, content);

    public Task WriteCodexPromptAsync(string content) => File.WriteAllTextAsync(CodexPromptPath, content);

    public async Task<BridgeStatusView> WaitForStateAsync(BridgeState expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(8));
        BridgeStatusView status;
        do
        {
            status = await Orchestrator.GetStatusAsync(CancellationToken.None);
            if (status.CurrentState == expected)
            {
                return status;
            }

            await Task.Delay(20);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Expected state {expected} but last observed {status.CurrentState}.");
    }

    /// <summary>Reads back whatever is currently persisted on disk — used to prove state survives an app restart.</summary>
    public Task<StateLoadResult> LoadPersistedStateAsync() => _stateStore.LoadAsync(CancellationToken.None);

    /// <summary>Writes a state snapshot directly, bypassing the orchestrator — used to simulate a crash that left an ambiguous mid-action state on disk.</summary>
    public Task SeedPersistedStateAsync(BridgeStateSnapshot snapshot) => _stateStore.SaveAsync(snapshot, CancellationToken.None);

    /// <summary>Builds a brand-new orchestrator instance against the SAME on-disk state/project, simulating an app restart.</summary>
    public AgentOrchestrator RebuildOrchestratorSimulatingRestart()
    {
        var configService = new StaticConfigurationService(Configuration);
        var gitService = new GitService(NullLogger<GitService>.Instance);
        var watcherFactory = new FileWatcherFactory(
            new FileWatcherOptions
            {
                DebounceMilliseconds = Configuration.FileDebounceMilliseconds,
                StabilityCheckIntervalMilliseconds = Configuration.FileStabilityCheckIntervalMilliseconds,
                RequiredConsecutiveStableChecks = Configuration.FileStabilityRequiredConsecutiveChecks,
                ReadRetryCount = Configuration.FileReadRetryCount,
                ReadRetryDelayMilliseconds = Configuration.FileReadRetryDelayMilliseconds,
            },
            NullLoggerFactory.Instance);

        return new AgentOrchestrator(
            new JsonStateStore(Path.Combine(AppDataDir, "AgentBridgeState.json"), NullLogger<JsonStateStore>.Instance),
            configService,
            watcherFactory,
            new SimpleAgentAdapterProvider(ClaudeAdapter, CodexAdapter),
            gitService,
            new PlaceholderTemplateEngine(),
            new ExponentialBackoffRetryPolicy(TimeProvider.System, NullLogger<ExponentialBackoffRetryPolicy>.Instance),
            new NullNotificationService(NullLogger<NullNotificationService>.Instance),
            new ProjectService(gitService),
            TimeProvider.System,
            NullLogger<AgentOrchestrator>.Instance);
    }

    private static void InitGitRepo(string dir)
    {
        RunGit(dir, "init", "-q", "-b", "main");
        RunGit(dir, "config", "user.email", "test@example.com");
        RunGit(dir, "config", "user.name", "Integration Test");
        File.WriteAllText(Path.Combine(dir, ".gitkeep"), "");
        RunGit(dir, "add", ".");
        RunGit(dir, "commit", "-q", "-m", "initial commit");
    }

    private static void RunGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    public void Dispose()
    {
        Orchestrator.Dispose();
        TryDelete(ProjectDir);
        TryDelete(AppDataDir);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

internal sealed class StaticConfigurationService(BridgeConfiguration configuration) : IConfigurationService
{
    public Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(configuration);

    public Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SimpleAgentAdapterProvider(FakeClaudeAdapter claude, FakeCodexAdapter codex) : IAgentAdapterProvider
{
    public IAgentAdapter GetAdapter(AgentRole role) => role switch
    {
        AgentRole.Claude => claude,
        AgentRole.Codex => codex,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
