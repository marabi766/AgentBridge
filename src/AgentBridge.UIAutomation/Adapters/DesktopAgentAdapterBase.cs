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
    private DateTimeOffset _lastAccessibilityActivationUtc = DateTimeOffset.MinValue;
    // How long a window may stay byte-identical while still showing a Stop
    // control before the bridge stops believing it. Long enough that a slow tool
    // call which renders nothing is not mistaken for a frozen frame.
    private static readonly TimeSpan StaleUiTimeout = TimeSpan.FromMinutes(10);
    private string? _lastActivityFingerprint;
    private DateTimeOffset _activityFingerprintSinceUtc = DateTimeOffset.UtcNow;
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
            var processing = HasConfiguredConversationMarker(elements, conversationIdentifier)
                && HasActiveProcessingControl(elements);
            return processing && !HasStoppedChanging(elements);
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

            try
            {
                window.SetForeground();
            }
            catch (Exception ex)
            {
                // A locked desktop has no foreground to hand out, and Windows
                // refuses the request. Delivery drives the window through UI
                // Automation rather than through the foreground, so this is not a
                // reason to abandon the handoff.
                _logger.LogDebug(ex, "Could not bring {Agent} to the foreground; continuing without it.", Name);
            }

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
            if (!HasUsableAccessibilityTree(descendants))
            {
                _logger.LogWarning(
                    "{Agent} exposed no renderer document after warm-up ({DescendantCount} descendants); status is indeterminate.",
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
            // The configured conversation does not have to be the one on screen.
            // The delivery path navigates to it and re-verifies before typing, so
            // a uniquely reachable sidebar entry is just as good as an open one.
            // Only a conversation that cannot be reached at all is indeterminate.
            var isCurrentConversation = HasConfiguredConversationMarker(descendants, conversationIdentifier);
            if (!isCurrentConversation && !HasUniqueConversationNavigationTarget(descendants, conversationIdentifier))
            {
                _logger.LogWarning(
                    "The configured {Agent} conversation '{Conversation}' is neither open nor uniquely reachable from the sidebar; status is indeterminate.",
                    Name, conversationIdentifier);
                return AgentStatus.Unknown;
            }

            // Only the conversation currently on screen can be proven to be
            // streaming. A background conversation never reports Busy here; the
            // orchestrator rechecks once it has been brought to the front. A Stop
            // control on a window that has stopped changing is a frozen frame, not
            // evidence of work, so it does not hold the agent in Busy either.
            if (isCurrentConversation
                && HasActiveProcessingControl(descendants)
                && !HasStoppedChanging(descendants))
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
        if (HasUsableAccessibilityTree(initial))
        {
            return initial;
        }

        // Electron enables the renderer accessibility tree asynchronously. A
        // second sample after one second is often enough, but Claude sometimes
        // needs another render turn while tools are streaming.
        var latest = initial;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            // Reacquire the root after Chromium has enabled its renderer AX tree;
            // querying through the original element can retain the shallow snapshot.
            var warmed = _automation.Value.FromHandle(windowHandle).FindAllDescendants();
            latest = warmed;
            if (HasUsableAccessibilityTree(warmed))
            {
                return warmed;
            }
        }

        // When an Electron window has stayed in its shell-only accessibility
        // mode, bringing it forward once prompts the renderer to expose its AX
        // tree. This is deliberately a last resort, only after the normal
        // passive warm-up failed, and is throttled so status refreshes do not
        // continuously steal focus from the operator.
        if (DateTimeOffset.UtcNow - _lastAccessibilityActivationUtc >= TimeSpan.FromSeconds(20))
        {
            try
            {
                var window = _automation.Value.FromHandle(windowHandle).AsWindow();
                if (window is not null)
                {
                    window.SetForeground();
                    _lastAccessibilityActivationUtc = DateTimeOffset.UtcNow;
                    await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
                    var foregroundWarmed = _automation.Value.FromHandle(windowHandle).FindAllDescendants();
                    if (HasUsableAccessibilityTree(foregroundWarmed))
                    {
                        _logger.LogInformation("Activated {Agent} once to recover its Electron accessibility tree.", Name);
                        return foregroundWarmed;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not activate {Agent} for accessibility recovery.", Name);
            }
        }

        return latest;
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
                Safe(() => element.Patterns.ExpandCollapse.IsSupported, false),
                conversationIdentifier));

    /// <summary>
    /// True once Chromium has published its renderer document, which is the point
    /// at which the window's real controls are visible to UI Automation.
    /// </summary>
    private static bool HasUsableAccessibilityTree(IEnumerable<AutomationElement> elements) =>
        elements.Any(element => ElementSemantics.IsRendererDocumentRoot(
            Safe(() => element.ControlType.ToString()),
            Safe(() => element.AutomationId)));

    /// <summary>
    /// Mirrors the navigation rule the conversation locator applies: exactly one
    /// sidebar entry may match the configured identifier, otherwise the target is
    /// ambiguous and must not be treated as reachable.
    /// </summary>
    private static bool HasUniqueConversationNavigationTarget(
        IEnumerable<AutomationElement> elements,
        string? conversationIdentifier) =>
        !string.IsNullOrWhiteSpace(conversationIdentifier)
        && elements.Count(element =>
            Safe(() => element.IsEnabled, false)
            && !Safe(() => element.IsOffscreen, true)
            && ElementSemantics.IsConversationNavigationCandidate(
                Safe(() => element.ControlType.ToString()),
                Safe(() => element.Name),
                Safe(() => element.ClassName),
                Safe(() => element.Patterns.ExpandCollapse.IsSupported, false),
                conversationIdentifier)) == 1;

    /// <summary>
    /// Decides whether a window that still shows a Stop control has simply frozen
    /// with it on screen.
    ///
    /// A disconnected desktop session was observed leaving both agents' windows
    /// rendered exactly as they were when the operator left: the agent finished
    /// its turn and wrote its protocol file, but Chromium never repainted, so the
    /// Stop button stayed and the bridge waited on it — three hours in one case,
    /// until signing back in repainted the window. Trusting that control alone
    /// therefore means an unattended run can stall forever on a picture of work.
    ///
    /// Real work moves something: streamed text, a tool card, a token counter. A
    /// tree that is byte-identical for minutes on end is a still image. This is
    /// only ever consulted while the orchestrator is already deferring a settled
    /// protocol file, so the agent's output is complete before staleness can
    /// release the wait.
    /// </summary>
    private bool HasStoppedChanging(IReadOnlyCollection<AutomationElement> elements)
    {
        var fingerprint = Fingerprint(elements);
        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(fingerprint, _lastActivityFingerprint, StringComparison.Ordinal))
        {
            _lastActivityFingerprint = fingerprint;
            _activityFingerprintSinceUtc = now;
            return false;
        }

        if (now - _activityFingerprintSinceUtc < StaleUiTimeout)
        {
            return false;
        }

        _logger.LogWarning(
            "{Agent} still shows a Stop control, but its window has not changed in {Minutes} minutes. "
            + "Treating it as idle: a disconnected desktop leaves the last frame on screen, and waiting "
            + "on it would stall the run indefinitely.",
            Name, (int)StaleUiTimeout.TotalMinutes);
        return true;
    }

    /// <summary>
    /// A cheap summary of what the window is showing. Names carry the streamed
    /// text, so anything the agent renders changes this.
    /// </summary>
    private static string Fingerprint(IReadOnlyCollection<AutomationElement> elements)
    {
        var hash = new HashCode();
        hash.Add(elements.Count);
        foreach (var element in elements)
        {
            hash.Add(Safe(() => element.Name, string.Empty), StringComparer.Ordinal);
        }

        return hash.ToHashCode().ToString("X8");
    }

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
