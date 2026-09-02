using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using AgentBridge.UIAutomation.Locators;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: AgentBridge.UIAutomation.Probe <process-name> [name-filter]");
    return 2;
}

using var process = Process.GetProcessesByName(args[0])
    .FirstOrDefault(candidate => candidate.MainWindowHandle != IntPtr.Zero);
if (process is null)
{
    Console.Error.WriteLine($"No visible main window found for '{args[0]}'.");
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
    var matchesFilter = args.Length == 2 && name.Contains(args[1], StringComparison.OrdinalIgnoreCase);
    if (!matchesFilter && type is not ("Edit" or "Document" or "Button" or "ListItem" or "TreeItem" or "TabItem" or "Pane"))
    {
        continue;
    }

    if (name.Length > 120) name = name[..120] + "…";
    var patterns = string.Join(',', element.GetSupportedPatterns().Select(pattern => pattern.Name));
    Console.WriteLine($"[{type}] Name='{name}' Id='{Safe(() => element.AutomationId)}' Class='{Safe(() => element.ClassName)}' " +
                      $"Enabled={Safe(() => element.IsEnabled.ToString())} Offscreen={Safe(() => element.IsOffscreen.ToString())} Patterns={patterns}");
}

if (args.Length == 2)
{
    var conversationLocator = new SemanticConversationLocator(NullLogger<SemanticConversationLocator>.Instance);
    var inputLocator = new SemanticInputLocator(NullLogger<SemanticInputLocator>.Instance);
    var conversation = await conversationLocator.FindConversationAsync(root, args[1], CancellationToken.None);
    var input = conversation is null
        ? null
        : await inputLocator.FindInputBoxAsync(conversation, CancellationToken.None);
    Console.WriteLine($"READ-ONLY VERIFICATION Conversation={(conversation is null ? "NOT FOUND" : "VERIFIED")} Input={(input is null ? "NOT FOUND" : "VERIFIED")}");
}

return 0;

static string Safe(Func<string?> read)
{
    try { return read() ?? string.Empty; }
    catch { return "<unavailable>"; }
}
