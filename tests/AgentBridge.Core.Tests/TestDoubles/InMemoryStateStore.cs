using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

public sealed class InMemoryStateStore : IStateStore
{
    private BridgeStateSnapshot? _snapshot;
    private bool _forceCorrupted;

    public int SaveCallCount { get; private set; }

    public void SeedLoaded(BridgeStateSnapshot snapshot) => _snapshot = snapshot;

    public void SeedCorrupted() => _forceCorrupted = true;

    public Task<StateLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (_forceCorrupted)
        {
            return Task.FromResult(StateLoadResult.Corrupted("simulated corruption", null));
        }

        return Task.FromResult(_snapshot is null ? StateLoadResult.NotFound() : StateLoadResult.Loaded(_snapshot));
    }

    public Task SaveAsync(BridgeStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        SaveCallCount++;
        _snapshot = snapshot;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        _snapshot = null;
        _forceCorrupted = false;
        return Task.CompletedTask;
    }

    public BridgeStateSnapshot? Current => _snapshot;
}
