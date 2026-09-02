using AgentBridge.Abstractions.Models;
using AgentBridge.Core.StateMachine;

namespace AgentBridge.Core.Tests.StateMachine;

public class BridgeStateMachineTests
{
    [Fact]
    public void FullHappyPathCycle_TransitionsThroughEveryState()
    {
        var sm = new BridgeStateMachine();
        var now = DateTimeOffset.UtcNow;

        Assert.True(sm.TryTransition(BridgeState.WaitingForClaudeReport, "start", now));
        Assert.True(sm.TryTransition(BridgeState.ClaudeReportDetected, "r", now));
        Assert.True(sm.TryTransition(BridgeState.WaitingForCodex, "r", now));
        Assert.True(sm.TryTransition(BridgeState.CodexProcessing, "r", now));
        Assert.True(sm.TryTransition(BridgeState.WaitingForCodexPrompt, "r", now));
        Assert.True(sm.TryTransition(BridgeState.CodexPromptDetected, "r", now));
        Assert.True(sm.TryTransition(BridgeState.WaitingForClaude, "r", now));
        Assert.True(sm.TryTransition(BridgeState.ClaudeProcessing, "r", now));
        // Loop back for the next iteration.
        Assert.True(sm.TryTransition(BridgeState.WaitingForClaudeReport, "r", now));

        Assert.Equal(BridgeState.WaitingForClaudeReport, sm.Current);
    }

    [Theory]
    [InlineData(BridgeState.Idle, BridgeState.ClaudeProcessing)]
    [InlineData(BridgeState.WaitingForClaudeReport, BridgeState.CodexProcessing)]
    [InlineData(BridgeState.ClaudeReportDetected, BridgeState.WaitingForClaude)]
    [InlineData(BridgeState.Stopped, BridgeState.Paused)]
    [InlineData(BridgeState.Error, BridgeState.WaitingForClaudeReport)]
    public void IllegalEdges_AreRejected(BridgeState from, BridgeState to)
    {
        var sm = new BridgeStateMachine(from);
        var accepted = sm.TryTransition(to, "illegal", DateTimeOffset.UtcNow);

        Assert.False(accepted);
        Assert.Equal(from, sm.Current); // never partially applies an illegal transition
    }

    [Fact]
    public void Transition_ToInvalidEdge_Throws()
    {
        var sm = new BridgeStateMachine(BridgeState.Idle);
        Assert.Throws<InvalidStateTransitionException>(() =>
            sm.Transition(BridgeState.ClaudeProcessing, "bad", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PauseThenResume_ReturnsToTheExactStateItWasPausedFrom()
    {
        var sm = new BridgeStateMachine();
        var now = DateTimeOffset.UtcNow;
        sm.TryTransition(BridgeState.WaitingForClaudeReport, "start", now);
        sm.TryTransition(BridgeState.ClaudeReportDetected, "r", now);
        sm.TryTransition(BridgeState.WaitingForCodex, "r", now);

        Assert.True(sm.TryTransition(BridgeState.Paused, "user paused", now));
        Assert.Equal(BridgeState.WaitingForCodex, sm.StateBeforePause);

        Assert.True(sm.TryTransition(BridgeState.WaitingForCodex, "resume", now));
        Assert.Equal(BridgeState.WaitingForCodex, sm.Current);
        Assert.Null(sm.StateBeforePause);
    }

    [Fact]
    public void StopIsReachable_FromEveryActiveState()
    {
        BridgeState[] activeStates =
        [
            BridgeState.Idle, BridgeState.WaitingForClaudeReport, BridgeState.ClaudeReportDetected,
            BridgeState.WaitingForCodex, BridgeState.CodexProcessing, BridgeState.WaitingForCodexPrompt,
            BridgeState.CodexPromptDetected, BridgeState.WaitingForClaude, BridgeState.ClaudeProcessing,
            BridgeState.Paused, BridgeState.Error,
        ];

        foreach (var state in activeStates)
        {
            var sm = new BridgeStateMachine(state);
            Assert.True(sm.TryTransition(BridgeState.Stopped, "stop", DateTimeOffset.UtcNow), $"Stop should be reachable from {state}");
        }
    }

    [Fact]
    public void ErrorCanBeReset_ToIdle_ButNotDirectlyIntoTheMiddleOfACycle()
    {
        var sm = new BridgeStateMachine(BridgeState.Error);
        Assert.True(BridgeStateMachine.IsValidTransition(BridgeState.Error, BridgeState.Idle));
        Assert.False(BridgeStateMachine.IsValidTransition(BridgeState.Error, BridgeState.CodexProcessing));
        Assert.True(sm.TryTransition(BridgeState.Idle, "reset", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void StateChanged_EventFires_WithPreviousAndNewState()
    {
        var sm = new BridgeStateMachine();
        BridgeStateChangedEventArgs? captured = null;
        sm.StateChanged += (_, e) => captured = e;

        sm.TryTransition(BridgeState.WaitingForClaudeReport, "start", DateTimeOffset.UtcNow);

        Assert.NotNull(captured);
        Assert.Equal(BridgeState.Idle, captured!.PreviousState);
        Assert.Equal(BridgeState.WaitingForClaudeReport, captured.NewState);
    }

    [Fact]
    public void ForceState_BypassesValidationForRecoveryScenarios()
    {
        var sm = new BridgeStateMachine();
        sm.ForceState(BridgeState.CodexProcessing, null);
        Assert.Equal(BridgeState.CodexProcessing, sm.Current);
    }
}
