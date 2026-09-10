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
public abstract class CommandLineAgentAdapter
    : IAgentAdapter, IReportsRunOutcome, IContinuesItsLastSession, IWaitsOutQuotaLimits, IDisposable
{
    /// <summary>
    /// Under this, a failed run cannot have done the work. These agents read a
    /// repository, edit files and write a report; none of that happens in a few
    /// seconds. The window is generous rather than tight because the cost of
    /// guessing wrong in each direction is not symmetric: retrying a genuine
    /// failure costs one wasted invocation, while stopping an overnight run for
    /// a refusal that fixed itself in a second costs the night.
    /// </summary>
    private static readonly TimeSpan CannotHaveWorkedWithin = TimeSpan.FromSeconds(60);

    private readonly IConfigurationService _configurationService;
    private readonly ILogger _logger;

    /// <summary>Settings, for subclasses whose agent offers something beyond one run.</summary>
    protected IConfigurationService Configuration => _configurationService;

    /// <summary>The same log the run machinery writes to, so one agent reads as one story.</summary>
    protected ILogger Log => _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private Process? _run;
    private Task<int>? _runCompletion;
    private string _lastOutput = string.Empty;
    private int _disposed;

    // Set when a run ends badly and too soon to have done anything. Read by the
    // orchestrator to tell a transient refusal from a real empty iteration.
    // Written by the observing task and read from the orchestration loop, so it
    // is volatile rather than plain.
    private volatile bool _lastRunFailedWithoutWorking;

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

    /// <summary>
    /// True only about a run that has finished. A run still in flight has not
    /// failed at anything yet, and saying otherwise would have the orchestrator
    /// resend underneath an agent that is still working.
    /// </summary>
    public bool LastRunFailedWithoutWorking =>
        _lastRunFailedWithoutWorking && _run is not { HasExited: false };

    /// <summary>Reads this agent's command line settings out of the configuration.</summary>
    protected abstract CommandLineInvocation ReadInvocation(BridgeConfiguration configuration);

    /// <summary>
    /// Rewrites the ordinary arguments into the ones that resume the last
    /// session. Each CLI spells this its own way, so each adapter says how.
    ///
    /// It is derived from the configured arguments rather than configured
    /// separately so that the two cannot drift: a model, sandbox or output
    /// format set for normal runs applies to a resumed one without being
    /// remembered in a second place.
    /// </summary>
    public abstract string ResumeArguments(string? arguments);

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

    public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken) =>
        StartRunAsync(message, resumeLastSession: false, cancellationToken);

    /// <summary>
    /// Sends the message into the session this agent was last having. Same run
    /// machinery, different arguments — which is the whole difference between
    /// "continue" meaning something and meaning nothing.
    /// </summary>
    public Task<bool> ContinueLastSessionAsync(string message, CancellationToken cancellationToken) =>
        StartRunAsync(message, resumeLastSession: true, cancellationToken);

    private async Task<bool> StartRunAsync(string message, bool resumeLastSession, CancellationToken cancellationToken)
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
            if (resumeLastSession)
            {
                invocation = invocation with { Arguments = ResumeArguments(invocation.Arguments) };
            }

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

            foreach (var (key, value) in ParseEnvironment(configuration.AgentEnvironment))
            {
                startInfo.Environment[key] = value;
            }

            var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogWarning("{Agent} delivery refused: the process could not be started.", Name);
                return false;
            }

            _run = process;
            _lastOutput = string.Empty;
            // Only ever describes the run that has finished. Leaving the previous
            // verdict standing would let one transient failure justify retrying
            // the next iteration too.
            _lastRunFailedWithoutWorking = false;
            _runCompletion = ObserveRunAsync(process, invocation.TimeoutSeconds, configuration.AgentLogLineLimit);

            // Writing the instruction and closing the stream is what starts the
            // work. Closing matters: the CLI reads the prompt from standard input,
            // so it waits for end-of-input before beginning.
            await process.StandardInput.WriteAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            _logger.LogInformation(
                resumeLastSession
                    ? "Delivered {Length} characters to {Agent} (pid {Pid}) in {Directory}, resuming its last session."
                    : "Delivered {Length} characters to {Agent} (pid {Pid}) in {Directory}.",
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
    /// Drops the announced reset, so the next status poll reports the agent as
    /// available. Used when the operator has signed the CLI into an account that
    /// still has allowance and the wait no longer means anything.
    /// </summary>
    public void ForgetAnnouncedQuotaWait()
    {
        var hadWait = false;
        lock (_quotaGate)
        {
            if (_quotaResetsAtUtc is not null)
            {
                _quotaResetsAtUtc = null;
                hadWait = true;
            }
        }

        if (hadWait)
        {
            _logger.LogInformation(
                "{Agent} allowance wait cleared on request; treating it as available again.", Name);
        }
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
    /// <summary>
    /// Reports what a run spent, once, in a line an operator can read.
    ///
    /// Logged outside the line budget on purpose: it is one line, it is the
    /// answer to "why is my allowance going", and it arrives at the end of a
    /// long run — which is precisely where the budget has already run out.
    /// </summary>
    private void NoteRunCost(string line)
    {
        var cost = AgentRunCostReader.TryRead(line);
        if (cost is null)
        {
            return;
        }

        _logger.LogInformation("{Agent} run cost: {Cost}", Name, cost.Describe());
    }

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
        var extraEnv = ParseEnvironment(configuration.AgentEnvironment);
        sb.AppendLine($"Extra environment     : {(extraEnv.Count == 0 ? "(none)" : string.Join(", ", extraEnv.Select(e => e.Key)))}");
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
    private async Task<int> ObserveRunAsync(Process process, int timeoutSeconds, int logLineLimit)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var transcript = new Transcript(this, process.Id, logLineLimit);
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
                var lasted = DateTimeOffset.UtcNow - startedAt;

                // An exhausted allowance already reports itself, and far better:
                // it knows when it lifts, where this only knows the run was too
                // short to have worked. Claiming it here as well would start a
                // blind retry against a wall that has hours left on it.
                _lastRunFailedWithoutWorking = lasted < CannotHaveWorkedWithin && !QuotaExhausted;

                _logger.LogWarning(
                    "{Agent} run (pid {Pid}) exited with code {ExitCode} after {Seconds:F1}s. Output tail: {Output}",
                    Name, process.Id, exitCode, lasted.TotalSeconds, Tail(_lastOutput, 400));

                if (_lastRunFailedWithoutWorking)
                {
                    _logger.LogWarning(
                        "{Agent} stopped after {Seconds:F1}s, too soon to have done the work, so nothing it was "
                        + "asked to do was started. Treating this as a transient failure rather than an empty iteration.",
                        Name, lasted.TotalSeconds);
                }
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
    private sealed class Transcript(CommandLineAgentAdapter adapter, int processId, int maximumLoggedLines)
    {

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

            // Detection reads the raw line: the refusal is a JSON field, and the
            // rendering below deliberately drops it as noise.
            adapter.NoteQuotaRefusal(line);
            adapter.NoteRunCost(line);

            // Rendered before the budget is spent, so the two thousand lines a run
            // may log are two thousand readable ones rather than two thousand
            // token counters.
            var readable = AgentStreamLine.Render(line);

            lock (_gate)
            {
                // The transcript keeps what actually came out. Diagnostics shows
                // it, so choosing what to log loses nothing.
                _text.AppendLine(line);
                if (_text.Length > MaximumRetainedCharacters)
                {
                    _text.Remove(0, _text.Length - MaximumRetainedCharacters);
                }

                if (readable is null)
                {
                    return;
                }

                if (maximumLoggedLines > 0 && _loggedLines >= maximumLoggedLines)
                {
                    if (!_saidItStoppedLogging)
                    {
                        _saidItStoppedLogging = true;
                        adapter._logger.LogInformation(
                            "{Agent} (pid {Pid}) has printed {Lines} lines; the rest is kept for diagnostics "
                            + "but no longer logged line by line. Raise \"Agent log line limit\" in Settings, "
                            + "or set it to 0, to keep logging.",
                            adapter.Name, processId, maximumLoggedLines);
                    }

                    return;
                }

                _loggedLines++;
            }

            adapter._logger.LogInformation(
                "{Agent} > {Line}",
                adapter.Name,
                readable.Length <= MaximumLineLength ? readable : readable[..MaximumLineLength] + "…");
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
    /// Parses the configured environment block: one <c>KEY=VALUE</c> per line.
    /// Blank lines and <c>#</c> comments are skipped; a line with no <c>=</c>, or
    /// an empty key, is ignored rather than failing the run. The value keeps
    /// everything after the first <c>=</c> verbatim, so <c>NODE_OPTIONS=--a --b</c>
    /// works.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseEnvironment(string? block)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(block))
        {
            return result;
        }

        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            result.Add(new KeyValuePair<string, string>(key, line[(separator + 1)..].Trim()));
        }

        return result;
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
