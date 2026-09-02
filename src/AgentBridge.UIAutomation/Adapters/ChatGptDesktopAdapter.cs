using AgentBridge.Abstractions.Models;
using AgentBridge.UIAutomation.Locators;
using Microsoft.Extensions.Logging;

namespace AgentBridge.UIAutomation.Adapters;

public sealed class ChatGptDesktopAdapter(
    string processName,
    string? executablePath,
    AgentBridge.Abstractions.Interfaces.IConfigurationService configurationService,
    IConversationLocator conversationLocator,
    IInputLocator inputLocator,
    IMessageSender messageSender,
    ILogger<ChatGptDesktopAdapter> logger)
    : DesktopAgentAdapterBase(
        "ChatGPT Desktop (Codex)", AgentRole.Codex, processName, executablePath, configurationService,
        conversationLocator, inputLocator, messageSender, logger);
