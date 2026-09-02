using AgentBridge.Abstractions.Models;

namespace AgentBridge.Fakes;

public sealed class FakeClaudeAdapter() : FakeAgentAdapterBase("Claude (Fake)", AgentRole.Claude);
