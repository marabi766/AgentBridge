using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Orchestration;
using AgentBridge.Core.Retry;
using AgentBridge.Core.Templates;
using AgentBridge.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Core.Tests.TestDoubles;

/// <summary>
/// Assembles a real <see cref="AgentOrchestrator"/> against fully-doubled
/// infrastructure and fake agent adapters, so orchestration logic (state
/// transitions, loop protection, iteration/max-iteration handling, dry run,
/// error/timeout paths) can be unit tested fast and deterministically without
/// any real file I/O or Windows UI Automation.
/// </summary>
public sealed class OrchestratorTestHarness
{
    public BridgeConfiguration Configuration { get; set; }

    public StubConfigurationService ConfigService { get; }

    public InMemoryStateStore StateStore { get; } = new();

    public ControllableFileWatcherFactory WatcherFactory { get; } = new();

    public FakeClaudeAdapter ClaudeAdapter { get; } = new();

    public FakeCodexAdapter CodexAdapter { get; } = new();

    public StubGitService GitService { get; } = new();

    public RecordingNotificationService NotificationService { get; } = new();

    public StubProjectService ProjectService { get; } = new();

    public AgentOrchestrator Orchestrator { get; }

    public OrchestratorTestHarness(BridgeConfiguration? configuration = null)
    {
        Configuration = configuration ?? BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = "C:/fake/project",
            DryRun = false,
            MaximumIterations = 50,
            AgentTimeoutSeconds = 2,
            RetryCount = 0,
            NotificationsEnabled = true,
        };
        ConfigService = new StubConfigurationService(Configuration);

        Orchestrator = new AgentOrchestrator(
            StateStore,
            ConfigService,
            WatcherFactory,
            new SimpleAgentAdapterProvider(ClaudeAdapter, CodexAdapter),
            GitService,
            new PlaceholderTemplateEngine(),
            new ExponentialBackoffRetryPolicy(TimeProvider.System, NullLogger<ExponentialBackoffRetryPolicy>.Instance),
            NotificationService,
            ProjectService,
            TimeProvider.System,
            NullLogger<AgentOrchestrator>.Instance);
    }

    public string ClaudeReportPath => ProjectService.GetClaudeReportFilePath(Configuration);

    public string CodexPromptPath => ProjectService.GetCodexPromptFilePath(Configuration);

    public ControllableFileWatcher ClaudeWatcher => WatcherFactory.Get(ClaudeReportPath);

    public ControllableFileWatcher CodexWatcher => WatcherFactory.Get(CodexPromptPath);

    public Task StartAsync() => Orchestrator.StartAsync(CancellationToken.None);

    /// <summary>
    /// File-change handlers run as fire-and-forget background work off the raised
    /// event, so tests poll for the expected terminal state instead of awaiting a
    /// Task directly. Fails fast and loudly if the orchestration never gets there.
    /// </summary>
    public async Task<BridgeStatusView> WaitForStateAsync(BridgeState expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        BridgeStatusView status;
        do
        {
            status = await Orchestrator.GetStatusAsync(CancellationToken.None);
            if (status.CurrentState == expected)
            {
                return status;
            }

            await Task.Delay(10);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Expected state {expected} but last observed {status.CurrentState} after waiting.");
    }
}
