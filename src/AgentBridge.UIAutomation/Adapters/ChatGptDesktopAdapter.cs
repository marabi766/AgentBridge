using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Adapters;

public sealed class ChatGptDesktopAdapter(string processName, string? executablePath, ILogger<ChatGptDesktopAdapter> logger)
    : DesktopAgentAdapterBase("ChatGPT Desktop (Codex)", AgentRole.Codex, processName, executablePath, logger);
