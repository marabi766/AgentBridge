using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Fakes;

public abstract class FakeAgentAdapterBase(string name, AgentRole role) : IAgentAdapter
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

    public async Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken)
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
        return true;
    }

    public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(State.Status);

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken) => Task.FromResult(
        $"Fake adapter '{Name}' ({Role}): Running={State.IsApplicationRunning}, Ready={State.IsReady}, " +
        $"MessagesSent={State.SentMessages.Count}, Status={State.Status}");
}
