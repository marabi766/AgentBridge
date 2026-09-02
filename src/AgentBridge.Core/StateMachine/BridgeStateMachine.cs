using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.StateMachine;

/// <summary>
/// Explicit finite state machine for the Agent Bridge orchestration cycle. This is
/// the single source of truth for "what state are we in" and "what transitions are
/// legal" — no scattered booleans anywhere else in the codebase should duplicate
/// this decision.
/// </summary>
public sealed class BridgeStateMachine
{
    private static readonly IReadOnlyDictionary<BridgeState, HashSet<BridgeState>> ValidTransitions =
        new Dictionary<BridgeState, HashSet<BridgeState>>
        {
            [BridgeState.Idle] = [BridgeState.WaitingForClaudeReport, BridgeState.Stopped],

            [BridgeState.WaitingForClaudeReport] =
                [BridgeState.ClaudeReportDetected, BridgeState.Paused, BridgeState.Stopped, BridgeState.Error],

            [BridgeState.ClaudeReportDetected] =
                [BridgeState.WaitingForCodex, BridgeState.Paused, BridgeState.Stopped, BridgeState.Error],

            [BridgeState.WaitingForCodex] =
                [BridgeState.CodexProcessing, BridgeState.Paused, BridgeState.Stopped, BridgeState.Error],

            [BridgeState.CodexProcessing] =
                [BridgeState.WaitingForCodexPrompt, BridgeState.Paused, BridgeState.Stopped, BridgeState.Error],

            [BridgeState.WaitingForCodexPrompt] =
                [BridgeState.CodexPromptDetected, BridgeState.Paused, BridgeState.Stopped, BridgeState.Error],

            [BridgeState.CodexPromptDetected] =
                [BridgeState.WaitingForClaude, BridgeState.Paused, BridgeState.Stopped, BridgeState.Error],

            [BridgeState.WaitingForClaude] =
                [BridgeState.ClaudeProcessing, BridgeState.Paused, BridgeState.Stopped, BridgeState.Error],

            [BridgeState.ClaudeProcessing] =
                [BridgeState.WaitingForClaudeReport, BridgeState.Stopped, BridgeState.Error],

            [BridgeState.Paused] =
            [
                BridgeState.WaitingForClaudeReport, BridgeState.ClaudeReportDetected,
                BridgeState.WaitingForCodex, BridgeState.CodexProcessing,
                BridgeState.WaitingForCodexPrompt, BridgeState.CodexPromptDetected,
                BridgeState.WaitingForClaude, BridgeState.ClaudeProcessing,
                BridgeState.Stopped,
            ],

            [BridgeState.Stopped] = [BridgeState.WaitingForClaudeReport],

            [BridgeState.Error] = [BridgeState.Idle, BridgeState.Stopped],
        };

    private readonly Lock _gate = new();

    public BridgeStateMachine(BridgeState initialState = BridgeState.Idle)
    {
        Current = initialState;
    }

    public BridgeState Current { get; private set; }

    /// <summary>Set right before entering Paused, so Resume knows where to go back to.</summary>
    public BridgeState? StateBeforePause { get; private set; }

    public event EventHandler<BridgeStateChangedEventArgs>? StateChanged;

    public static bool IsValidTransition(BridgeState from, BridgeState to) =>
        ValidTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Attempts the transition. Returns false (without throwing) if the edge is illegal.</summary>
    public bool TryTransition(BridgeState to, string reason, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!IsValidTransition(Current, to))
            {
                return false;
            }

            var previous = Current;

            if (to == BridgeState.Paused)
            {
                StateBeforePause = previous;
            }
            else if (previous == BridgeState.Paused)
            {
                StateBeforePause = null;
            }

            Current = to;
            StateChanged?.Invoke(this, new BridgeStateChangedEventArgs
            {
                PreviousState = previous,
                NewState = to,
                Reason = reason,
                OccurredAtUtc = nowUtc,
            });
            return true;
        }
    }

    /// <summary>Same as <see cref="TryTransition"/> but throws for programmer errors (a caller that should already know the edge is invalid).</summary>
    public void Transition(BridgeState to, string reason, DateTimeOffset nowUtc)
    {
        if (!TryTransition(to, reason, nowUtc))
        {
            throw new InvalidStateTransitionException(Current, to);
        }
    }

    /// <summary>Restores state from a persisted snapshot without validating an edge (used only on application startup recovery).</summary>
    public void ForceState(BridgeState state, BridgeState? stateBeforePause)
    {
        lock (_gate)
        {
            Current = state;
            StateBeforePause = stateBeforePause;
        }
    }
}
