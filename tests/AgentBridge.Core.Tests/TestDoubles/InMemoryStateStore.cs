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
        // JsonStateStore.SaveAsync starts by awaiting its file lock with this
        // same token, so a caller who passes one already cancelled never reaches
        // the write at all. A double that ignored that, as this one once did,
        // would let a real bug — a "final" persist made to depend on a token
        // that a step just above it had already cancelled — pass every test
        // while silently losing the write in production. See
        // AgentOrchestratorFailurePersistenceTests for what that cost.
        cancellationToken.ThrowIfCancellationRequested();
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
