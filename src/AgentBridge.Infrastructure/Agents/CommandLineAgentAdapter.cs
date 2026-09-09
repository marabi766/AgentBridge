using System.Diagnostics;
using System.Text;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// How one non-interactive agent run is spelled: what to execute, with which
/// arguments, and how long it may take. Read from configuration at the moment a
/// run starts, because the exact flags belong to the CLI rather than to this
/// codebase and will change without asking us.
/// </summary>
public sealed record CommandLineInvocation
{
    public required string ConfiguredExecutable { get; init; }

    public required string? Arguments { get; init; }

    public required int TimeoutSeconds { get; init; }

    /// <summary>What to tell the operator when the executable cannot be found.</summary>
    public required string InstallHint { get; init; }
}

/// <summary>
/// Drives an agent as a command line process rather than through its desktop
/// window.
///
/// Every delivery fault this bridge has had came from the same place: reading a
/// GUI that is allowed to lag, freeze, re-render or hold a leftover draft. A
/// pipe has none of those states. The instruction goes to stdin, the process
/// either exits zero or it does not, and "is it still working" is answered by
/// whether the process is alive rather than by what a button looks like. It also
/// keeps running while the desktop is locked, which no window-reading path can.
///
/// Several members of <see cref="IAgentAdapter"/> exist only because a window
/// has to be found and focused. Here there is no window, so they succeed
/// immediately — that is the point of this adapter, not an omission.
/// </summary>
public abstract class CommandLineAgentAdapter : IAgentAdapter, IDisposable
{
    private readonly IConfigurationService _configurationService;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private Process? _run;
    private Task<int>? _runCompletion;
    private string _lastOutput = string.Empty;
    private int _disposed;

    // When this agent said it would have allowance again. Kept across runs on
    // purpose: the run that hit the limit has already exited by the time anyone
    // asks, so forgetting it with the process would lose the only evidence.
    // Guarded because both output pumps can report a refusal at once.
    private readonly Lock _quotaGate = new();
    private DateTimeOffset? _quotaResetsAtUtc;

    protected CommandLineAgentAdapter(IConfigurationService configurationService, ILogger logger)
    {
        _configurationService = configurationService;
        _logger = logger;
    }

    public abstract string Name { get; }

    public abstract AgentRole Role { get; }

    /// <summary>
    /// The process exiting is a real, checkable delivery receipt — stronger than
    /// anything the desktop path can observe — so a live run is allowed.
    /// </summary>
    public bool SupportsRealMessageDelivery => true;

    /// <summary>Reads this agent's command line settings out of the configuration.</summary>
    protected abstract CommandLineInvocation ReadInvocation(BridgeConfiguration configuration);

    public async Task<bool> IsApplicationRunningAsync(CancellationToken cancellationToken)
    {
        // "Running" for a command means "can be run": there is no resident
        // process to find between invocations.
        var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
        return ResolveExecutable(ReadInvocation(configuration).ConfiguredExecutable) is not null;
    }

    public Task<bool> LaunchApplicationAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) =>
        IsApplicationRunningAsync(cancellationToken);

    public Task<bool> IsProcessingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = _run;
        return Task.FromResult(run is not null && !run.HasExited);
    }

    public Task<bool> ActivateAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>No window and no conversation list, so there is nothing to locate.</summary>
    public Task<bool> FindConversationAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>No editor to find: the instruction is written to standard input.</summary>
    public Task<bool> FindInputBoxAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _logger.LogWarning("{Agent} delivery refused: the message is empty.", Name);
            return false;
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_run is { HasExited: false })
            {
                _logger.LogWarning("{Agent} delivery refused: a run started earlier has not finished.", Name);
                return false;
            }

            var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var invocation = ReadInvocation(configuration);
            var executable = ResolveExecutable(invocation.ConfiguredExecutable);
            if (executable is null)
            {
                _logger.LogWarning(
                    "{Agent} delivery refused: '{Executable}' was not found. {Hint}",
                    Name, invocation.ConfiguredExecutable, invocation.InstallHint);
                return false;
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                // The agent acts on its working directory, so the run has to start
                // where the project is rather than wherever the bridge was launched.
                WorkingDirectory = configuration.ProjectPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in SplitArguments(invocation.Arguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogWarning("{Agent} delivery refused: the process could not be started.", Name);
                return false;
            }

            _run = process;
            _lastOutput = string.Empty;
            _runCompletion = ObserveRunAsync(process, invocation.TimeoutSeconds);

            // Writing the instruction and closing the stream is what starts the
            // work. Closing matters: the CLI reads the prompt from standard input,
            // so it waits for end-of-input before beginning.
            await process.StandardInput.WriteAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            _logger.LogInformation(
                "Delivered {Length} characters to {Agent} (pid {Pid}) in {Directory}.",
                message.Length, Name, process.Id, configuration.ProjectPath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Agent} delivery failed to start.", Name);
            return false;
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var run = _run;
        if (run is not null && !run.HasExited)
        {
            return AgentStatus.Busy;
        }

        if (QuotaExhausted)
        {
            return AgentStatus.RateLimited;
        }

        return await IsApplicationRunningAsync(cancellationToken).ConfigureAwait(false)
            ? AgentStatus.Ready
            : AgentStatus.NotRunning;
    }

    /// <summary>
    /// Whether this agent is still inside a refusal it announced. Reporting it as
    /// a distinct status rather than as a failure is what lets the orchestrator
    /// wait the reset out instead of ending the run — an exhausted allowance is a
    /// delay, not a fault.
    /// </summary>
    private bool QuotaExhausted
    {
        get
        {
            bool waitIsOver;
            lock (_quotaGate)
            {
                if (_quotaResetsAtUtc is null)
                {
                    return false;
                }

                if (DateTimeOffset.UtcNow < _quotaResetsAtUtc)
                {
                    return true;
                }

                // Clearing it under the same lock is what makes the message below
                // happen once rather than on every status poll.
                _quotaResetsAtUtc = null;
                waitIsOver = true;
            }

            if (waitIsOver)
            {
                _logger.LogInformation(
                    "{Agent} allowance should have reset; treating it as available again.", Name);
            }

            return false;
        }
    }

    /// <summary>How long to wait when an agent refuses without saying when it will stop refusing.</summary>
    private static readonly TimeSpan UnknownQuotaResetWait = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Records a refusal the agent printed. Only ever extends the wait: two lines
    /// of one refusal must not shorten it, and a later, further-off reset is the
    /// one to believe.
    /// </summary>
    private void NoteQuotaRefusal(string line)
    {
        if (!AgentQuotaSignal.TryDetect(line, out var announcedResetUtc))
        {
            return;
        }

        var resetsAt = announcedResetUtc ?? DateTimeOffset.UtcNow + UnknownQuotaResetWait;
        lock (_quotaGate)
        {
            if (_quotaResetsAtUtc is not null && _quotaResetsAtUtc >= resetsAt)
            {
                return;
            }

            _quotaResetsAtUtc = resetsAt;
        }

        _logger.LogWarning(
            "{Agent} has no allowance left and will not work until {ResetsAt:u} (in about {Minutes:F0} minutes). "
            + "The run waits rather than failing. It said: {Line}",
            Name, resetsAt, (resetsAt - DateTimeOffset.UtcNow).TotalMinutes,
            line.Length <= 300 ? line : line[..300]);
    }

    public async Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var invocation = ReadInvocation(configuration);
        var executable = ResolveExecutable(invocation.ConfiguredExecutable);
        var sb = new StringBuilder();
        sb.AppendLine($"=== {Name} ===");
        sb.AppendLine($"Configured executable : {invocation.ConfiguredExecutable}");
        sb.AppendLine($"Resolved to           : {executable ?? "(not found on PATH)"}");
        sb.AppendLine($"Arguments             : {invocation.Arguments}");
        sb.AppendLine($"Run timeout           : {invocation.TimeoutSeconds}s");
        sb.AppendLine($"Working directory     : {configuration.ProjectPath}");
        sb.AppendLine($"Run in flight         : {(_run is { HasExited: false } ? $"yes (pid {_run.Id})" : "no")}");
        sb.AppendLine($"Allowance             : {(_quotaResetsAtUtc is { } resets ? $"exhausted until {resets:u}" : "available")}");
        if (_lastOutput.Length > 0)
        {
            sb.AppendLine("--- last run output (tail) ---");
            sb.AppendLine(Tail(_lastOutput, 2000));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Drains both streams and reports how the run ended. Draining is not
    /// optional: a process whose output nobody reads blocks once its pipe buffer
    /// fills, which would look exactly like an agent that never finishes.
    ///
    /// Each line is logged as it arrives rather than collected and reported at
    /// the end. With no window to look at, an agent that has been working for
    /// twenty minutes and one that hung after two are otherwise indistinguishable
    /// until it exits.
    /// </summary>
    private async Task<int> ObserveRunAsync(Process process, int timeoutSeconds)
    {
        try
        {
            var transcript = new Transcript(this, process.Id);
            var stdout = PumpAsync(process.StandardOutput, transcript);
            var stderr = PumpAsync(process.StandardError, transcript);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "{Agent} run (pid {Pid}) exceeded {Timeout}s and was stopped.", Name, process.Id, timeoutSeconds);
                TryKill(process);
            }

            // Both pumps end when their stream closes, which the exit above has
            // just caused. Awaiting them is what guarantees the transcript is
            // complete before it is read.
            await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            _lastOutput = transcript.ToString();
            var exitCode = process.HasExited ? process.ExitCode : -1;
            if (exitCode == 0)
            {
                _logger.LogInformation("{Agent} run (pid {Pid}) finished.", Name, process.Id);
            }
            else
            {
                _logger.LogWarning(
                    "{Agent} run (pid {Pid}) exited with code {ExitCode}. Output tail: {Output}",
                    Name, process.Id, exitCode, Tail(_lastOutput, 400));
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while observing the {Agent} run.", Name);
            return -1;
        }
    }

    private static async Task PumpAsync(StreamReader reader, Transcript transcript)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            transcript.Add(line);
        }
    }

    /// <summary>
    /// One run's output: logged line by line as it arrives, and kept as a bounded
    /// tail for diagnostics.
    ///
    /// Both bounds exist because the volume is the agent's to decide, not ours. A
    /// run that prints a progress bar or a large diff would otherwise fill the
    /// day's log file and hold its whole output in memory, and neither is a
    /// price worth paying for visibility into a run that is going fine.
    /// </summary>
    private sealed class Transcript(CommandLineAgentAdapter adapter, int processId)
    {
        private const int MaximumLoggedLines = 2000;

        // Generous, because an agent asked for machine-readable output emits one
        // JSON object per line and cutting it produces something no reader can
        // parse — the truncation would cost exactly the lines worth reading. Still
        // bounded: one run cannot write more than roughly eight megabytes.
        private const int MaximumLineLength = 4000;
        private const int MaximumRetainedCharacters = 32_000;

        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();
        private int _loggedLines;
        private bool _saidItStoppedLogging;

        public void Add(string line)
        {
            if (line.Length == 0)
            {
                return;
            }

            adapter.NoteQuotaRefusal(line);

            lock (_gate)
            {
                _text.AppendLine(line);
                if (_text.Length > MaximumRetainedCharacters)
                {
                    _text.Remove(0, _text.Length - MaximumRetainedCharacters);
                }

                if (_loggedLines >= MaximumLoggedLines)
                {
                    if (!_saidItStoppedLogging)
                    {
                        _saidItStoppedLogging = true;
                        adapter._logger.LogInformation(
                            "{Agent} (pid {Pid}) has printed {Lines} lines; the rest is kept for diagnostics "
                            + "but no longer logged line by line.",
                            adapter.Name, processId, MaximumLoggedLines);
                    }

                    return;
                }

                _loggedLines++;
            }

            adapter._logger.LogInformation(
                "{Agent} > {Line}",
                adapter.Name,
                line.Length <= MaximumLineLength ? line : line[..MaximumLineLength] + "…");
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop the {Agent} run.", Name);
        }
    }

    /// <summary>
    /// Finds the executable, accepting either a bare command to look up on PATH
    /// or a full path. On Windows the command is usually a .cmd shim written by
    /// npm, which is why the extensions are tried rather than the bare name only.
    /// </summary>
    public static string? ResolveExecutable(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (Path.IsPathRooted(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", string.Empty }
            : new[] { string.Empty };

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), configured + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth failing over.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Splits the configured argument string, honouring double quotes so a value
    /// containing spaces can be given as one argument.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }

        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string Tail(string text, int max) =>
        text.Length <= max ? text : text[^max..];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var run = _run;
        if (run is not null)
        {
            TryKill(run);
            run.Dispose();
        }

        _runLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
