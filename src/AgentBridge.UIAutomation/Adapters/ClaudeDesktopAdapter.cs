using AgentBridge.Abstractions.Models;
using AgentBridge.UIAutomation.Locators;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Adapters;

public sealed class ClaudeDesktopAdapter(
    string processName,
    string? executablePath,
    AgentBridge.Abstractions.Interfaces.IConfigurationService configurationService,
    IConversationLocator conversationLocator,
    IInputLocator inputLocator,
    IMessageSender messageSender,
    ILogger<ClaudeDesktopAdapter> logger)
    : DesktopAgentAdapterBase(
        "Claude Desktop", AgentRole.Claude, processName, executablePath, configurationService,
        conversationLocator, inputLocator, messageSender, logger);
