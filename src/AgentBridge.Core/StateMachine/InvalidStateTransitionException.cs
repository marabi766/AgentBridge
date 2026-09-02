using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.StateMachine;

public sealed class InvalidStateTransitionException : Exception
{
    public BridgeState From { get; }

    public BridgeState To { get; }

    public InvalidStateTransitionException(BridgeState from, BridgeState to)
        : base($"Invalid state transition: {from} -> {to} is not a permitted edge in the Agent Bridge state machine.")
    {
        From = from;
        To = to;
    }
}
