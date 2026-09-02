using FlaUI.Core.AutomationElements;

namespace AgentBridge.UIAutomation.Locators;

/// <summary>
/// Fine-grained UI Automation abstractions consumed only by adapters in this
/// project — orchestration code never sees these, only <see cref="AgentBridge.Abstractions.Interfaces.IAgentAdapter"/>.
/// Concrete implementations are intentionally deferred to the UI Automation
/// implementation phase (see UI_AUTOMATION.md); this phase only establishes the
/// layered-selector architecture so real selector logic can be dropped in later
/// without touching the orchestrator.
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
    Task<bool> SendAsync(AutomationElement inputBox, string message, CancellationToken cancellationToken);
}
