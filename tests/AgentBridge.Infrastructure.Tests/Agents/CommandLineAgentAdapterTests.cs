using System.Text;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Agents;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

/// <summary>
/// Exercises the real process plumbing against a real command. `sort` is on
/// every Windows machine and does exactly what is needed here: it reads standard
/// input and writes it back a line at a time, so one run proves the instruction
/// reached the process and the output came back.
/// </summary>
public sealed class CommandLineAgentAdapterTests
{
    [Fact]
    public async Task TheInstructionReachesTheProcessAndItsOutputIsLoggedAsItArrives()
    {
        var logger = new CapturingLogger();
        using var adapter = new SortAdapter(logger);

        Assert.True(await adapter.SendMessageAsync("beta\nalpha\n", CancellationToken.None));
        await adapter.WaitForTheRunToFinishAsync();

        // Logged line by line, in the order the process produced them — not
        // collected and dumped after it exited.
        var streamed = logger.Messages.Where(message => message.Contains(" > ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["sort > alpha", "sort > beta"], streamed);
    }

    [Fact]
    public async Task ARunThatIsStillGoingIsReportedAsProcessing()
    {
        var logger = new CapturingLogger();
        using var adapter = new SortAdapter(logger);

        Assert.False(await adapter.IsProcessingAsync(CancellationToken.None));

        await adapter.SendMessageAsync("only\n", CancellationToken.None);
        await adapter.WaitForTheRunToFinishAsync();

        Assert.False(await adapter.IsProcessingAsync(CancellationToken.None));
        Assert.Equal(AgentStatus.Ready, await adapter.GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyInstructionIsRefusedRatherThanStartingAnAgentWithNothingToDo()
    {
        var logger = new CapturingLogger();
        using var adapter = new SortAdapter(logger);

        Assert.False(await adapter.SendMessageAsync("   ", CancellationToken.None));
        Assert.Contains(logger.Messages, message => message.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AMissingExecutableSaysHowToInstallItInsteadOfFailingSilently()
    {
        var logger = new CapturingLogger();
        using var adapter = new SortAdapter(logger, executable: "definitely-not-a-real-command");

        Assert.False(await adapter.SendMessageAsync("anything", CancellationToken.None));
        Assert.Contains(logger.Messages, message => message.Contains("install it with", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SortAdapter : CommandLineAgentAdapter
    {
        private readonly string _executable;

        public SortAdapter(ILogger logger, string executable = "sort")
            : base(new FixedConfigurationService(), logger) => _executable = executable;

        public override string Name => "sort";

        public override AgentRole Role => AgentRole.Codex;

        /// <summary>sort has no sessions; the flag is enough to see it was used.</summary>
        public override string ResumeArguments(string? arguments) => $"--resumed {arguments}".Trim();

        /// <summary>
        /// The run is observed on a background task, exactly as it is in
        /// production. A test that asserted on its output without waiting would
        /// be racing it.
        /// </summary>
        public async Task WaitForTheRunToFinishAsync()
        {
            while (await IsProcessingAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await Task.Delay(20).ConfigureAwait(false);
            }

            // The process has exited; the pumps get one more moment to drain what
            // is still sitting in the pipe.
            await Task.Delay(200).ConfigureAwait(false);
        }

        protected override CommandLineInvocation ReadInvocation(BridgeConfiguration configuration) => new()
        {
            ConfiguredExecutable = _executable,
            Arguments = null,
            TimeoutSeconds = 30,
            InstallHint = "Install it with a package manager.",
        };
    }

    private sealed class FixedConfigurationService : IConfigurationService
    {
        public Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BridgeConfiguration { ProjectPath = Path.GetTempPath() });

        public Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<string> _messages = [];

        public List<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
