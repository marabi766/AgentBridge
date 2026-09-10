using System.Diagnostics;
using System.Text;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// Drives Claude Code through <c>claude --print</c> rather than through the
/// Claude desktop window. Everything about running the process lives in
/// <see cref="CommandLineAgentAdapter"/>; only which settings to read is
/// Claude-specific.
/// </summary>
public sealed class ClaudeCliAdapter : CommandLineAgentAdapter, IOpensARemoteControlSession
{
    public ClaudeCliAdapter(IConfigurationService configurationService, ILogger<ClaudeCliAdapter> logger)
        : base(configurationService, logger)
    {
    }

    public override string Name => "Claude CLI";

    public override AgentRole Role => AgentRole.Claude;

    protected override CommandLineInvocation ReadInvocation(BridgeConfiguration configuration) => new()
    {
        ConfiguredExecutable = configuration.ClaudeCliExecutable,
        Arguments = configuration.ClaudeCliArguments,
        TimeoutSeconds = configuration.ClaudeCliTimeoutSeconds,
        InstallHint = "Install it with \"npm install -g @anthropic-ai/claude-code\" and sign in, "
            + "or point Claude CLI executable at its full path.",
    };

    /// <summary>
    /// <c>--continue</c> picks up the most recent conversation in the working
    /// directory, which is the project — so the session it resumes is the one
    /// that was working on this repository, not whatever ran last elsewhere.
    /// </summary>
    public override string ResumeArguments(string? arguments) =>
        Contains(arguments, "--continue") || Contains(arguments, "-c")
            ? arguments!
            : $"--continue {arguments}".TrimEnd();

    private static bool Contains(string? arguments, string flag) =>
        SplitArguments(arguments).Contains(flag, StringComparer.Ordinal);

    /// <summary>
    /// Starts a background session with Remote Control on and reads back the
    /// link it publishes.
    ///
    /// Two things about the CLI shape this. <c>--remote-control</c> only means
    /// anything to an interactive session, and a session whose output is
    /// redirected is not interactive — so this cannot simply be run and read.
    /// <c>--bg</c> is the way through: it starts the session in a terminal of
    /// its own and returns an id immediately. The link is then drawn inside that
    /// terminal, which <c>claude logs</c> prints back.
    ///
    /// <c>--continue</c> makes the session a copy of the conversation the bridge
    /// was last having here, so the operator opens it already holding everything
    /// the agent had done rather than an empty prompt.
    /// </summary>
    public async Task<RemoteControlSession?> OpenRemoteControlSessionAsync(CancellationToken cancellationToken)
    {
        var configuration = await Configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
        var executable = ResolveExecutable(configuration.ClaudeCliExecutable);
        if (executable is null)
        {
            Log.LogWarning(
                "Remote control refused: '{Executable}' was not found.", configuration.ClaudeCliExecutable);
            return null;
        }

        var name = string.IsNullOrWhiteSpace(configuration.ClaudeRemoteControlSessionName)
            ? "Agent Bridge"
            : configuration.ClaudeRemoteControlSessionName.Trim();

        // A directory Claude has never run in has no conversation to continue,
        // and asking for one there fails. Falling back to a fresh session is
        // better than refusing: the operator still gets a session on the project.
        var started = await RunAsync(
            executable, configuration, ["--bg", "--remote-control", name, "--continue"], cancellationToken)
            .ConfigureAwait(false);
        var sessionId = RemoteControlSignal.ReadSessionId(started);
        if (sessionId is null)
        {
            Log.LogInformation("No conversation here to continue; opening a fresh remote control session.");
            started = await RunAsync(
                executable, configuration, ["--bg", "--remote-control", name], cancellationToken)
                .ConfigureAwait(false);
            sessionId = RemoteControlSignal.ReadSessionId(started);
        }

        if (sessionId is null)
        {
            Log.LogWarning(
                "Remote control refused: the session did not report an id. Output: {Output}", Tail(started, 400));
            return null;
        }

        Log.LogInformation("Remote control session {Session} started; waiting for its link.", sessionId);

        // The link appears in the session's own terminal a moment after it
        // connects, so the log is read until it does rather than once.
        var deadline = DateTimeOffset.UtcNow
            + TimeSpan.FromSeconds(Math.Max(5, configuration.RemoteControlLinkTimeoutSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            var logs = await RunAsync(executable, configuration, ["logs", sessionId], cancellationToken)
                .ConfigureAwait(false);
            var url = RemoteControlSignal.ReadRemoteControlUrl(logs);
            if (url is not null)
            {
                Log.LogInformation("Remote control session {Session} is at {Url}.", sessionId, url);
                return new RemoteControlSession
                {
                    SessionId = sessionId,
                    Url = url,
                    OpenedAtUtc = DateTimeOffset.UtcNow,
                };
            }
        }

        // The session is running and costing an allowance, but nobody can reach
        // it and nobody was told its id. Stop it rather than leave it stranded.
        Log.LogWarning(
            "Remote control session {Session} published no link within {Timeout}s; stopping it.",
            sessionId, configuration.RemoteControlLinkTimeoutSeconds);
        await RunAsync(executable, configuration, ["stop", sessionId], CancellationToken.None).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Runs one short <c>claude</c> command and returns everything it printed.
    /// Separate from the run machinery in the base class, which is built around
    /// one long agent run fed on standard input; these are quick questions whose
    /// whole answer is their output.
    /// </summary>
    private static async Task<string> RunAsync(
        string executable,
        BridgeConfiguration configuration,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = configuration.ProjectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in ParseEnvironment(configuration.AgentEnvironment))
        {
            startInfo.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return string.Empty;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            // Bounded so a command that never returns cannot hold the operation
            // open; every one of these answers in seconds or not at all.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return string.Empty;
            }

            return string.Concat(
                await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string Tail(string text, int max) =>
        text.Length <= max ? text : text[^max..];
}
