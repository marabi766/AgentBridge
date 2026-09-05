using System.Diagnostics;
using System.Text;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// Drives Codex as a command line process rather than through the ChatGPT
/// desktop window.
///
/// Every delivery fault this bridge has had came from the same place: reading a
/// GUI that is allowed to lag, freeze, re-render or hold a leftover draft. A
/// pipe has none of those states. The instruction goes to stdin, the process
/// either exits zero or it does not, and "is it still working" is answered by
/// whether the process is alive rather than by what a button looks like.
///
/// Several members of <see cref="IAgentAdapter"/> exist only because a window
/// has to be found and focused. Here there is no window, so they succeed
/// immediately — that is the point of this adapter, not an omission.
/// </summary>
public sealed class CodexCliAdapter : IAgentAdapter, IDisposable
{
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<CodexCliAdapter> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private Process? _run;
    private Task<int>? _runCompletion;
    private string _lastOutput = string.Empty;
    private int _disposed;

    public CodexCliAdapter(IConfigurationService configurationService, ILogger<CodexCliAdapter> logger)
    {
        _configurationService = configurationService;
        _logger = logger;
    }

    public string Name => "Codex CLI";

    public AgentRole Role => AgentRole.Codex;

    /// <summary>
    /// The process exiting is a real, checkable delivery receipt — stronger than
    /// anything the desktop path can observe — so a live run is allowed.
    /// </summary>
    public bool SupportsRealMessageDelivery => true;

    public async Task<bool> IsApplicationRunningAsync(CancellationToken cancellationToken)
    {
        // "Running" for a command means "can be run": there is no resident
        // process to find between invocations.
        var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
        return ResolveExecutable(configuration) is not null;
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
            _logger.LogWarning("Codex CLI delivery refused: the message is empty.");
            return false;
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_run is { HasExited: false })
            {
                _logger.LogWarning("Codex CLI delivery refused: a run started earlier has not finished.");
                return false;
            }

            var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var executable = ResolveExecutable(configuration);
            if (executable is null)
            {
                _logger.LogWarning(
                    "Codex CLI delivery refused: '{Executable}' was not found. Install it with "
                    + "\"npm install -g @openai/codex\" and sign in, or point Codex CLI executable at its full path.",
                    configuration.CodexCliExecutable);
                return false;
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                // Codex acts on its working directory, so the run has to start
                // where the project is rather than wherever the bridge was launched.
                WorkingDirectory = configuration.ProjectPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in SplitArguments(configuration.CodexCliArguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogWarning("Codex CLI delivery refused: the process could not be started.");
                return false;
            }

            _run = process;
            _lastOutput = string.Empty;
            _runCompletion = ObserveRunAsync(process, configuration.CodexCliTimeoutSeconds);

            // Writing the instruction and closing the stream is what starts the
            // work. Closing matters: the default arguments tell Codex to read the
            // prompt from stdin, so it waits for end-of-input before beginning.
            await process.StandardInput.WriteAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            _logger.LogInformation(
                "Delivered {Length} characters to Codex CLI (pid {Pid}) in {Directory}.",
                message.Length, process.Id, configuration.ProjectPath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Codex CLI delivery failed to start.");
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

        return await IsApplicationRunningAsync(cancellationToken).ConfigureAwait(false)
            ? AgentStatus.Ready
            : AgentStatus.NotRunning;
    }

    public async Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var executable = ResolveExecutable(configuration);
        var sb = new StringBuilder();
        sb.AppendLine($"=== {Name} ===");
        sb.AppendLine($"Configured executable : {configuration.CodexCliExecutable}");
        sb.AppendLine($"Resolved to           : {executable ?? "(not found on PATH)"}");
        sb.AppendLine($"Arguments             : {configuration.CodexCliArguments}");
        sb.AppendLine($"Working directory     : {configuration.ProjectPath}");
        sb.AppendLine($"Run in flight         : {(_run is { HasExited: false } ? $"yes (pid {_run.Id})" : "no")}");
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
    /// </summary>
    private async Task<int> ObserveRunAsync(Process process, int timeoutSeconds)
    {
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Codex CLI run (pid {Pid}) exceeded {Timeout}s and was stopped.", process.Id, timeoutSeconds);
                TryKill(process);
            }

            _lastOutput = string.Concat(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            var exitCode = process.HasExited ? process.ExitCode : -1;
            if (exitCode == 0)
            {
                _logger.LogInformation("Codex CLI run (pid {Pid}) finished.", process.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Codex CLI run (pid {Pid}) exited with code {ExitCode}. Output tail: {Output}",
                    process.Id, exitCode, Tail(_lastOutput, 400));
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while observing the Codex CLI run.");
            return -1;
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
            _logger.LogDebug(ex, "Could not stop the Codex CLI run.");
        }
    }

    /// <summary>
    /// Finds the executable, accepting either a bare command to look up on PATH
    /// or a full path. On Windows the command is usually a .cmd shim written by
    /// npm, which is why the extensions are tried rather than the bare name only.
    /// </summary>
    private static string? ResolveExecutable(BridgeConfiguration configuration)
    {
        var configured = configuration.CodexCliExecutable;
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
    }
}
