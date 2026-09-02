using AgentBridge.Abstractions.Models;

namespace AgentBridge.Fakes;

public sealed class FakeCodexAdapter() : FakeAgentAdapterBase("Codex (Fake)", AgentRole.Codex);
