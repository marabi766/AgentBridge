using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using AgentBridge.UIAutomation.Locators;
using Microsoft.Extensions.Logging.Abstractions;

var sendFileMode = args.Length == 4 && string.Equals(args[0], "--send-file", StringComparison.Ordinal);
var sendTextMode = args.Length == 4 && string.Equals(args[0], "--send-text", StringComparison.Ordinal);
var sendMode = sendFileMode || sendTextMode;
if (!sendMode && args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: AgentBridge.UIAutomation.Probe <process-name> [name-filter]");
    Console.Error.WriteLine("   or: AgentBridge.UIAutomation.Probe --send-file <process-name> <exact-title> <message-file>");
    Console.Error.WriteLine("   or: AgentBridge.UIAutomation.Probe --send-text <process-name> <exact-title> <message>");
    return 2;
}

var processName = sendMode ? args[1] : args[0];
var titleFilter = sendMode ? args[2] : args.Length == 2 ? args[1] : null;
var messageFile = sendFileMode ? Path.GetFullPath(args[3]) : null;
if (sendFileMode && !File.Exists(messageFile))
{
    Console.Error.WriteLine($"Message file does not exist: {messageFile}");
    return 2;
}

using var process = Process.GetProcessesByName(processName)
    .FirstOrDefault(candidate => candidate.MainWindowHandle != IntPtr.Zero);
if (process is null)
{
    Console.Error.WriteLine($"No visible main window found for '{processName}'.");
    return 1;
}

using var automation = new UIA3Automation();
var root = automation.FromHandle(process.MainWindowHandle);

// Chromium exposes its accessibility tree lazily. The first read warms it up.
_ = root.FindAllDescendants();
await Task.Delay(TimeSpan.FromSeconds(2));

var elements = root.FindAllDescendants();
Console.WriteLine($"Process={process.ProcessName} PID={process.Id} Title='{process.MainWindowTitle}' Elements={elements.Length}");

foreach (var element in elements)
{
    var type = Safe(() => element.ControlType.ToString());
    var name = Safe(() => element.Name).Replace('\r', ' ').Replace('\n', ' ');
    var matchesFilter = titleFilter is not null && name.Contains(titleFilter, StringComparison.OrdinalIgnoreCase);
    if (!matchesFilter && type is not ("Edit" or "Document" or "Button" or "ListItem" or "TreeItem" or "TabItem" or "Pane"))
    {
        continue;
    }

    if (name.Length > 120) name = name[..120] + "…";
    var patterns = string.Join(',', element.GetSupportedPatterns().Select(pattern => pattern.Name));
    Console.WriteLine($"[{type}] Name='{name}' Id='{Safe(() => element.AutomationId)}' Class='{Safe(() => element.ClassName)}' " +
                      $"Enabled={Safe(() => element.IsEnabled.ToString())} Offscreen={Safe(() => element.IsOffscreen.ToString())} Patterns={patterns}");
}

if (titleFilter is not null)
{
    var conversationLocator = new SemanticConversationLocator(NullLogger<SemanticConversationLocator>.Instance);
    var inputLocator = new SemanticInputLocator(NullLogger<SemanticInputLocator>.Instance);
    var conversation = await conversationLocator.FindConversationAsync(root, titleFilter, CancellationToken.None);
    var input = conversation is null
        ? null
        : await inputLocator.FindInputBoxAsync(conversation, CancellationToken.None);
    Console.WriteLine($"READ-ONLY VERIFICATION Conversation={(conversation is null ? "NOT FOUND" : "VERIFIED")} Input={(input is null ? "NOT FOUND" : "VERIFIED")}");

    if (sendMode)
    {
        if (conversation is null || input is null)
        {
            Console.Error.WriteLine("SEND REFUSED: target conversation or input was not uniquely verified.");
            return 1;
        }

        var sender = new VerifiedMessageSender(new ProbeLogger<VerifiedMessageSender>());
        var message = sendFileMode ? await File.ReadAllTextAsync(messageFile!) : args[3];
        var delivered = await sender.SendAsync(conversation, input, message, CancellationToken.None);
        Console.WriteLine($"SEND VERIFICATION: {(delivered ? "DELIVERED" : "NOT VERIFIED")}");
        return delivered ? 0 : 1;
    }
}

return 0;

static string Safe(Func<string?> read)
{
    try { return read() ?? string.Empty; }
    catch { return "<unavailable>"; }
}

file sealed class ProbeLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Console.Error.WriteLine($"{logLevel}: {formatter(state, exception)}");
}
