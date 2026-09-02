using System.Diagnostics;
using System.Text;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Adapters;

/// <summary>
/// Shared plumbing for driving one Windows GUI application via UI Automation.
///
/// Scope for this phase: process detection, launch, and window activation are real
/// (cheap, verified against the actually-installed Claude Desktop / ChatGPT Desktop
/// on this machine — see UI_AUTOMATION.md). Conversation discovery, input-box
/// discovery, and message sending are intentionally NOT implemented yet — both apps
/// are Chromium/Electron-based and only expose their web-content accessibility tree
/// after a UI Automation client "warms up" the renderer (see UI_AUTOMATION.md for
/// the exact behavior observed). Building reliable selectors for that tree is real
/// UI Automation implementation work, deferred to a later phase per the backend-first
/// plan. Calling these methods now returns false with a clear log entry — it never
/// throws and never pretends to have sent something it didn't.
/// </summary>
public abstract class DesktopAgentAdapterBase : IAgentAdapter, IDisposable
{
    private readonly ILogger _logger;
    private readonly Lazy<UIA3Automation> _automation = new(() => new UIA3Automation());

    protected DesktopAgentAdapterBase(
        string name,
        AgentRole role,
        string processName,
        string? executablePath,
        ILogger logger)
    {
        Name = name;
        Role = role;
        ProcessName = processName;
        ExecutablePath = executablePath;
        _logger = logger;
    }

    public string Name { get; }

    public AgentRole Role { get; }

    public bool SupportsRealMessageDelivery => false;

    protected string ProcessName { get; }

    protected string? ExecutablePath { get; }

    private static IReadOnlyList<Process> GetCandidateProcesses(string processName) =>
        Process.GetProcessesByName(processName).Where(p => p.MainWindowHandle != IntPtr.Zero).ToList();

    public Task<bool> IsApplicationRunningAsync(CancellationToken cancellationToken)
    {
        using var processes = new ProcessListDisposer(GetCandidateProcesses(ProcessName));
        return Task.FromResult(processes.Processes.Count > 0);
    }

    public Task<bool> LaunchApplicationAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath) || !File.Exists(ExecutablePath))
        {
            _logger.LogWarning("Cannot launch {Agent}: no valid executable path configured ('{Path}').", Name, ExecutablePath);
            return Task.FromResult(false);
        }

        try
        {
            Process.Start(new ProcessStartInfo(ExecutablePath) { UseShellExecute = true });
            _logger.LogInformation("Launched {Agent} from {Path}.", Name, ExecutablePath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch {Agent} from {Path}.", Name, ExecutablePath);
            return Task.FromResult(false);
        }
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        using var processes = new ProcessListDisposer(GetCandidateProcesses(ProcessName));
        var process = processes.Processes.FirstOrDefault();
        if (process is null)
        {
            return Task.FromResult(false);
        }

        try
        {
            var window = _automation.Value.FromHandle(process.MainWindowHandle);
            var ready = window.Properties.IsEnabled.ValueOrDefault && !window.Properties.IsOffscreen.ValueOrDefault;
            return Task.FromResult(ready);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query readiness for {Agent}.", Name);
            return Task.FromResult(false);
        }
    }

    public Task<bool> ActivateAsync(CancellationToken cancellationToken)
    {
        using var processes = new ProcessListDisposer(GetCandidateProcesses(ProcessName));
        var process = processes.Processes.FirstOrDefault();
        if (process is null)
        {
            _logger.LogWarning("Cannot activate {Agent}: no running process with a main window found.", Name);
            return Task.FromResult(false);
        }

        try
        {
            var element = _automation.Value.FromHandle(process.MainWindowHandle);
            var window = element.AsWindow();
            if (window is null)
            {
                _logger.LogWarning("Cannot activate {Agent}: main window element is not a Window control type.", Name);
                return Task.FromResult(false);
            }

            window.SetForeground();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to activate {Agent}.", Name);
            return Task.FromResult(false);
        }
    }

    public Task<bool> FindConversationAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "{Agent}.FindConversationAsync is not implemented yet — conversation discovery is deferred to " +
            "the UI Automation implementation phase (see UI_AUTOMATION.md). Use Dry Run for now.", Name);
        return Task.FromResult(false);
    }

    public Task<bool> FindInputBoxAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "{Agent}.FindInputBoxAsync is not implemented yet — input box discovery is deferred to " +
            "the UI Automation implementation phase (see UI_AUTOMATION.md). Use Dry Run for now.", Name);
        return Task.FromResult(false);
    }

    public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "{Agent}.SendMessageAsync is not implemented yet — message delivery is deferred to " +
            "the UI Automation implementation phase (see UI_AUTOMATION.md). No message was sent. Use Dry Run for now.", Name);
        return Task.FromResult(false);
    }

    public async Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!await IsApplicationRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return AgentStatus.NotRunning;
        }

        return await IsReadyAsync(cancellationToken).ConfigureAwait(false) ? AgentStatus.Ready : AgentStatus.Unreachable;
    }

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        using var processes = new ProcessListDisposer(GetCandidateProcesses(ProcessName));

        if (processes.Processes.Count == 0)
        {
            sb.AppendLine($"{Name}: no running process named '{ProcessName}' with a main window was found.");
            return Task.FromResult(sb.ToString());
        }

        foreach (var process in processes.Processes)
        {
            sb.AppendLine($"=== {Name}: PID={process.Id} Title='{process.MainWindowTitle}' Path='{TryGetPath(process)}' ===");
            try
            {
                var root = _automation.Value.FromHandle(process.MainWindowHandle);
                DumpElement(root, 0, 3, sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  ERROR reading automation tree: {ex.Message}");
            }
        }

        return Task.FromResult(sb.ToString());
    }

    private static string TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "(unknown)";
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static void DumpElement(AutomationElement element, int depth, int maxDepth, StringBuilder sb)
    {
        if (depth > maxDepth)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        string name = "", controlType = "?", automationId = "", className = "";
        try { name = element.Properties.Name.ValueOrDefault ?? ""; } catch { /* best-effort diagnostics */ }
        try { controlType = element.Properties.ControlType.ValueOrDefault.ToString(); } catch { /* best-effort diagnostics */ }
        try { automationId = element.Properties.AutomationId.ValueOrDefault ?? ""; } catch { /* best-effort diagnostics */ }
        try { className = element.Properties.ClassName.ValueOrDefault ?? ""; } catch { /* best-effort diagnostics */ }

        sb.AppendLine($"{indent}[{controlType}] Name='{name}' AutomationId='{automationId}' Class='{className}'");

        AutomationElement[] children;
        try
        {
            children = element.FindAllChildren();
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            DumpElement(child, depth + 1, maxDepth, sb);
        }
    }

    public void Dispose()
    {
        if (_automation.IsValueCreated)
        {
            _automation.Value.Dispose();
        }
    }

    /// <summary>Disposes the Process handles obtained via Process.GetProcessesByName without disposing the processes themselves.</summary>
    private readonly struct ProcessListDisposer : IDisposable
    {
        public ProcessListDisposer(IReadOnlyList<Process> processes) => Processes = processes;

        public IReadOnlyList<Process> Processes { get; }

        public void Dispose()
        {
            foreach (var process in Processes)
            {
                process.Dispose();
            }
        }
    }
}
