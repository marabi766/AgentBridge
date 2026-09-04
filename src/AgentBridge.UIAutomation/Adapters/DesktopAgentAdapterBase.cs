using System.Diagnostics;
using System.Text;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.UIAutomation.Locators;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Adapters;

/// <summary>
/// Shared plumbing for driving one Windows GUI application via UI Automation.
///
/// Process/window control plus semantic, receipt-verifying delivery for one Windows
/// desktop agent. Every ambiguous selector fails closed and Send is invoked once.
/// </summary>
public abstract class DesktopAgentAdapterBase : IAgentAdapter, IDisposable
{
    private readonly ILogger _logger;
    private readonly IConfigurationService _configurationService;
    private readonly IConversationLocator _conversationLocator;
    private readonly IInputLocator _inputLocator;
    private readonly IMessageSender _messageSender;
    private readonly Lazy<UIA3Automation> _automation = new(() => new UIA3Automation());
    private AutomationElement? _conversation;
    private AutomationElement? _inputBox;
    private int _disposed;

    protected DesktopAgentAdapterBase(
        string name,
        AgentRole role,
        string processName,
        string? executablePath,
        IConfigurationService configurationService,
        IConversationLocator conversationLocator,
        IInputLocator inputLocator,
        IMessageSender messageSender,
        ILogger logger)
    {
        Name = name;
        Role = role;
        ProcessName = processName;
        ExecutablePath = executablePath;
        _configurationService = configurationService;
        _conversationLocator = conversationLocator;
        _inputLocator = inputLocator;
        _messageSender = messageSender;
        _logger = logger;
    }

    public string Name { get; }

    public AgentRole Role { get; }

    public bool SupportsRealMessageDelivery => true;

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

    public async Task<bool> IsProcessingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var processes = new ProcessListDisposer(GetCandidateProcesses(ProcessName));
        var process = processes.Processes.FirstOrDefault();
        if (process is null)
        {
            return false;
        }

        try
        {
            var elements = await GetWarmedDescendantsAsync(process.MainWindowHandle, cancellationToken).ConfigureAwait(false);
            // Chromium/Electron initially exposes an intentionally shallow UIA
            // tree. The first descendant query enables AXMode; a later query
            // exposes the real controls. Never treat that first empty tree as
            // evidence that a running agent is idle.
            var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var conversationIdentifier = Role == AgentRole.Claude
                ? configuration.ClaudeConversationIdentifier
                : configuration.CodexConversationIdentifier;

            // A desktop app can have another task streaming in the background.
            // It must never make the configured bridge conversation appear busy.
            // If the current conversation cannot be proven from the live tree,
            // GetStatusAsync will return Unknown and the orchestrator will wait.
            return HasConfiguredConversationMarker(elements, conversationIdentifier)
                && HasActiveProcessingControl(elements);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query processing state for {Agent}.", Name);
            return false;
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

    public async Task<bool> FindConversationAsync(CancellationToken cancellationToken)
    {
        _conversation = null;
        _inputBox = null;
        using var processes = new ProcessListDisposer(GetCandidateProcesses(ProcessName));
        var process = processes.Processes.FirstOrDefault();
        if (process is null)
        {
            return false;
        }

        try
        {
            var window = _automation.Value.FromHandle(process.MainWindowHandle);
            var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var conversationIdentifier = Role == AgentRole.Claude
                ? configuration.ClaudeConversationIdentifier
                : configuration.CodexConversationIdentifier;
            _conversation = await _conversationLocator.FindConversationAsync(
                window, conversationIdentifier, cancellationToken).ConfigureAwait(false);
            return _conversation is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to locate the configured {Agent} conversation.", Name);
            return false;
        }
    }

    public async Task<bool> FindInputBoxAsync(CancellationToken cancellationToken)
    {
        _inputBox = null;
        if (_conversation is null)
        {
            _logger.LogWarning("Cannot locate {Agent} input before verifying the configured conversation.", Name);
            return false;
        }

        _inputBox = await _inputLocator.FindInputBoxAsync(_conversation, cancellationToken).ConfigureAwait(false);
        return _inputBox is not null;
    }

    public async Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        if (_conversation is null || _inputBox is null)
        {
            _logger.LogWarning("Cannot send to {Agent}: conversation and input must first be uniquely verified.", Name);
            return false;
        }

        return await _messageSender.SendAsync(_conversation, _inputBox, message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var processes = new ProcessListDisposer(GetCandidateProcesses(ProcessName));
        var process = processes.Processes.FirstOrDefault();
        if (process is null)
        {
            return AgentStatus.NotRunning;
        }

        try
        {
            var window = _automation.Value.FromHandle(process.MainWindowHandle);
            if (!window.Properties.IsEnabled.ValueOrDefault || window.Properties.IsOffscreen.ValueOrDefault)
            {
                return AgentStatus.Unreachable;
            }

            var descendants = await GetWarmedDescendantsAsync(process.MainWindowHandle, cancellationToken).ConfigureAwait(false);
            if (!ElementSemantics.HasUsableDesktopAccessibilityTree(descendants.Length))
            {
                _logger.LogWarning(
                    "{Agent} accessibility tree remained shallow after warm-up ({DescendantCount} descendants); status is indeterminate.",
                    Name, descendants.Length);
                // The desktop app is present and its window is responsive; only
                // the Chromium accessibility renderer is not ready to prove its
                // activity. Report Unknown rather than Unreachable so operators
                // see the distinction while orchestration still fails closed.
                return AgentStatus.Unknown;
            }

            var configuration = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var conversationIdentifier = Role == AgentRole.Claude
                ? configuration.ClaudeConversationIdentifier
                : configuration.CodexConversationIdentifier;
            if (!HasConfiguredConversationMarker(descendants, conversationIdentifier))
            {
                _logger.LogWarning(
                    "The configured {Agent} conversation '{Conversation}' could not be verified in the current window; status is indeterminate.",
                    Name, conversationIdentifier);
                return AgentStatus.Unknown;
            }

            var processing = descendants.Any(element =>
                Safe(() => element.IsEnabled, false)
                && !Safe(() => element.IsOffscreen, true)
                && ElementSemantics.IsProcessingButton(
                    Safe(() => element.ControlType.ToString()), Safe(() => element.Name)));
            if (processing)
            {
                return AgentStatus.Busy;
            }

            // Claude exposes these three signals together while it is waiting for
            // an automatic session-limit reset. Requiring the combination avoids
            // mistaking an older rendered chat message for the current status.
            var quotaLimited = descendants.Any(element =>
                    !Safe(() => element.IsOffscreen, true)
                    && ElementSemantics.IsQuotaLimitMarker(Safe(() => element.Name)))
                && descendants.Any(element =>
                    !Safe(() => element.IsOffscreen, true)
                    && ElementSemantics.IsQuotaResetMarker(Safe(() => element.Name)))
                && descendants.Any(element =>
                    !Safe(() => element.IsOffscreen, true)
                    && ElementSemantics.IsSendButton(
                        Safe(() => element.ControlType.ToString()), Safe(() => element.Name))
                    && !Safe(() => element.IsEnabled, true));

            return quotaLimited ? AgentStatus.RateLimited : AgentStatus.Ready;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query status for {Agent}.", Name);
            return AgentStatus.Unreachable;
        }
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

    private static T Safe<T>(Func<T> read, T fallback = default!)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private async Task<AutomationElement[]> GetWarmedDescendantsAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken)
    {
        var initial = _automation.Value.FromHandle(windowHandle).FindAllDescendants();
        if (ElementSemantics.HasUsableDesktopAccessibilityTree(initial.Length))
        {
            return initial;
        }

        // Electron enables the renderer accessibility tree asynchronously. A
        // second sample after one second is often enough, but Claude sometimes
        // needs another render turn while tools are streaming.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            // Reacquire the root after Chromium has enabled its renderer AX tree;
            // querying through the original element can retain the shallow snapshot.
            var warmed = _automation.Value.FromHandle(windowHandle).FindAllDescendants();
            if (ElementSemantics.HasUsableDesktopAccessibilityTree(warmed.Length) || attempt == 1)
            {
                return warmed;
            }
        }

        return initial;
    }

    private static bool HasConfiguredConversationMarker(
        IEnumerable<AutomationElement> elements,
        string? conversationIdentifier) =>
        !string.IsNullOrWhiteSpace(conversationIdentifier)
        && elements.Any(element =>
            Safe(() => element.IsEnabled, false)
            && !Safe(() => element.IsOffscreen, true)
            && ElementSemantics.IsCurrentConversationMarker(
                Safe(() => element.ControlType.ToString()),
                Safe(() => element.Name),
                Safe(() => element.ClassName),
                conversationIdentifier));

    private static bool HasActiveProcessingControl(IEnumerable<AutomationElement> elements) =>
        elements.Any(element =>
            Safe(() => element.IsEnabled, false)
            && !Safe(() => element.IsOffscreen, true)
            && ElementSemantics.IsProcessingButton(
                Safe(() => element.ControlType.ToString()), Safe(() => element.Name)));

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
