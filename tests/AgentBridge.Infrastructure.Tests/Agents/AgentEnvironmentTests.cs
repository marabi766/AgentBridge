using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Agents;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

/// <summary>
/// The environment block exists for settings an agent would otherwise have to
/// rediscover on every run — on a proxied machine, the Node flags that let
/// corepack reach the registry and skip a sandbox-hostile pre-command check.
/// </summary>
public sealed class AgentEnvironmentTests
{
    [Fact]
    public void OneKeyValuePerLineIsParsed()
    {
        var parsed = CommandLineAgentAdapter.ParseEnvironment(
            "NODE_OPTIONS=--use-env-proxy\nPNPM_CONFIG_VERIFY_DEPS_BEFORE_RUN=false");

        Assert.Equal(
            [
                new KeyValuePair<string, string>("NODE_OPTIONS", "--use-env-proxy"),
                new KeyValuePair<string, string>("PNPM_CONFIG_VERIFY_DEPS_BEFORE_RUN", "false"),
            ],
            parsed);
    }

    [Fact]
    public void EverythingAfterTheFirstEqualsIsTheValue()
    {
        // NODE_OPTIONS carries its own "=" and spaces; only the first "=" splits.
        var parsed = CommandLineAgentAdapter.ParseEnvironment("NODE_OPTIONS=--max-old-space-size=4096 --use-env-proxy");

        var one = Assert.Single(parsed);
        Assert.Equal("NODE_OPTIONS", one.Key);
        Assert.Equal("--max-old-space-size=4096 --use-env-proxy", one.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void NothingConfiguredIsNoVariables(string? block) =>
        Assert.Empty(CommandLineAgentAdapter.ParseEnvironment(block));

    [Fact]
    public void BlankLinesCommentsAndMalformedLinesAreSkippedRatherThanFailing()
    {
        var parsed = CommandLineAgentAdapter.ParseEnvironment(
            """
            # a proxied machine

            NODE_OPTIONS=--use-env-proxy
            this line has no equals
            =valueWithNoKey
            PNPM_CONFIG_VERIFY_DEPS_BEFORE_RUN=false
            """);

        Assert.Equal(
            [
                new KeyValuePair<string, string>("NODE_OPTIONS", "--use-env-proxy"),
                new KeyValuePair<string, string>("PNPM_CONFIG_VERIFY_DEPS_BEFORE_RUN", "false"),
            ],
            parsed);
    }

    [Fact]
    public void SurroundingWhitespaceOnKeyAndValueIsTrimmed()
    {
        var one = Assert.Single(CommandLineAgentAdapter.ParseEnvironment("  FOO  =  bar  "));
        Assert.Equal("FOO", one.Key);
        Assert.Equal("bar", one.Value);
    }

    [Fact]
    public async Task TheParsedVariablesReachTheAgentProcess()
    {
        var logger = new CollectingLogger();
        using var adapter = new EchoEnvAdapter(
            logger, "AB_ENV_TEST=reached-the-process\nSECOND=also-here");

        Assert.True(await adapter.SendMessageAsync("go", CancellationToken.None));
        await adapter.WaitUntilIdleAsync();

        var output = string.Join("\n", logger.Messages);
        Assert.Contains("reached-the-process", output);
        Assert.Contains("also-here", output);
    }

    /// <summary>Runs <c>cmd /c set</c> so its output shows the process's own environment.</summary>
    private sealed class EchoEnvAdapter(ILogger logger, string environment) : CommandLineAgentAdapter(
        new FixedConfig(environment), logger)
    {
        public override string Name => "echo-env";

        public override AgentRole Role => AgentRole.Codex;

        public override string ResumeArguments(string? arguments) => arguments ?? string.Empty;

        public async Task WaitUntilIdleAsync()
        {
            while (await IsProcessingAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await Task.Delay(20).ConfigureAwait(false);
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        protected override CommandLineInvocation ReadInvocation(BridgeConfiguration configuration) => new()
        {
            ConfiguredExecutable = "cmd.exe",
            Arguments = "/c set",
            TimeoutSeconds = 30,
            InstallHint = "cmd.exe is part of Windows.",
        };
    }

    private sealed class FixedConfig(string environment) : IConfigurationService
    {
        public Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BridgeConfiguration
            {
                ProjectPath = Path.GetTempPath(),
                AgentEnvironment = environment,
            });

        public Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CollectingLogger : ILogger
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
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
