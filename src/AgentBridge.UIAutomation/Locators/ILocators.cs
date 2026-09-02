using FlaUI.Core.AutomationElements;

namespace AgentBridge.UIAutomation.Locators;

/// <summary>
/// Fine-grained UI Automation abstractions consumed only by adapters in this
/// project — orchestration code never sees these, only <see cref="AgentBridge.Abstractions.Interfaces.IAgentAdapter"/>.
/// Selectors are deliberately semantic and fail closed. They do not use screen
/// coordinates, localized DOM paths, or a "first matching element" fallback.
/// </summary>
public interface IWindowLocator
{
    Task<AutomationElement?> FindMainWindowAsync(string processName, CancellationToken cancellationToken);
}

public interface IConversationLocator
{
    Task<AutomationElement?> FindConversationAsync(AutomationElement mainWindow, string? conversationIdentifier, CancellationToken cancellationToken);
}

public interface IInputLocator
{
    Task<AutomationElement?> FindInputBoxAsync(AutomationElement conversation, CancellationToken cancellationToken);
}

public interface IMessageSender
{
    Task<bool> SendAsync(AutomationElement conversation, AutomationElement inputBox, string message, CancellationToken cancellationToken);
}
