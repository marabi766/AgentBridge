using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.StateMachine;

public sealed class BridgeStateChangedEventArgs : EventArgs
{
    public required BridgeState PreviousState { get; init; }

    public required BridgeState NewState { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}
