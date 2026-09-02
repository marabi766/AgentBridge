using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Adapters;

public sealed class ClaudeDesktopAdapter(string processName, string? executablePath, ILogger<ClaudeDesktopAdapter> logger)
    : DesktopAgentAdapterBase("Claude Desktop", AgentRole.Claude, processName, executablePath, logger);
