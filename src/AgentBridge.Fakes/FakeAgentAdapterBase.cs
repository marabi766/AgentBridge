using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Fakes;

public abstract class FakeAgentAdapterBase(string name, AgentRole role)
    : IAgentAdapter, IReportsRunOutcome, IContinuesItsLastSession, IOpensARemoteControlSession, IWaitsOutQuotaLimits
{
    public string Name { get; } = name;

    public AgentRole Role { get; } = role;

    public virtual bool SupportsRealMessageDelivery => false;

    /// <summary>Test-controlled behavior and a record of every message actually "sent".</summary>
    public FakeAgentAdapterState State { get; } = new();

    public Task<bool> IsApplicationRunningAsync(CancellationToken cancellationToken)
    {
        State.IsApplicationRunningCallCount++;
        return Task.FromResult(State.IsApplicationRunning);
    }

    public Task<bool> LaunchApplicationAsync(CancellationToken cancellationToken)
    {
        if (State.LaunchSucceeds)
        {
            State.IsApplicationRunning = true;
        }

        return Task.FromResult(State.LaunchSucceeds);
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        State.IsReadyCallCount++;
        return Task.FromResult(State.IsReady);
    }

    public Task<bool> IsProcessingAsync(CancellationToken cancellationToken) =>
        Task.FromResult(State.IsProcessing);

    public Task<bool> ActivateAsync(CancellationToken cancellationToken)
    {
        State.ActivateCallCount++;
        return Task.FromResult(State.ActivateSucceeds);
    }

    public Task<bool> FindConversationAsync(CancellationToken cancellationToken)
    {
        State.FindConversationCallCount++;
        return Task.FromResult(State.FindConversationSucceeds);
    }

    public Task<bool> FindInputBoxAsync(CancellationToken cancellationToken)
    {
        State.FindInputBoxCallCount++;
        return Task.FromResult(State.FindInputBoxSucceeds);
    }

    public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken) =>
        DeliverAsync(message, resumed: false, cancellationToken);

    /// <summary>Records that the session was resumed, so a test can tell the two apart.</summary>
    public Task<bool> ContinueLastSessionAsync(string message, CancellationToken cancellationToken) =>
        DeliverAsync(message, resumed: true, cancellationToken);

    private async Task<bool> DeliverAsync(string message, bool resumed, CancellationToken cancellationToken)
    {
        State.SendMessageCallCount++;

        if (State.SendMessageDelay > TimeSpan.Zero)
        {
            await Task.Delay(State.SendMessageDelay, cancellationToken).ConfigureAwait(false);
        }

        if (!State.SendMessageSucceeds)
        {
            return false;
        }

        State.SentMessages.Add(message);
        if (resumed)
        {
            State.ResumedMessages.Add(message);
        }

        if (State.BecomesBusyOnSend)
        {
            State.IsProcessing = true;
        }

        return true;
    }

    public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(State.Status);

    /// <summary>
    /// Mirrors the real adapter: an operator override clears the rate-limited
    /// status so the run is treated as able to proceed again.
    /// </summary>
    public void ForgetAnnouncedQuotaWait()
    {
        State.ForgetAnnouncedQuotaWaitCallCount++;
        if (State.Status == AgentStatus.RateLimited)
        {
            State.Status = AgentStatus.Ready;
        }
    }

    public bool LastRunFailedWithoutWorking => State.LastRunFailedWithoutWorking;

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken) => Task.FromResult(
        $"Fake adapter '{Name}' ({Role}): Running={State.IsApplicationRunning}, Ready={State.IsReady}, " +
        $"MessagesSent={State.SentMessages.Count}, Status={State.Status}");
    /// <summary>
    /// Hands back whatever the test set up, so the orchestrator's own behaviour
    /// around a session — publishing the link, leaving the cycle alone — can be
    /// checked without a real agent.
    /// </summary>
    public Task<RemoteControlSession?> OpenRemoteControlSessionAsync(CancellationToken cancellationToken)
    {
        State.RemoteControlCallCount++;
        return Task.FromResult(State.RemoteControlSession);
    }

}
