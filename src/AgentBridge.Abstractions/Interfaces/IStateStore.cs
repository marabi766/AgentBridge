using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// Atomic persistence for <see cref="BridgeStateSnapshot"/>. Never throws on a
/// corrupted file — reports it via <see cref="StateLoadResult"/> so the caller can
/// fail safely instead of blindly resuming automation from unknown state.
/// </summary>
public interface IStateStore
{
    Task<StateLoadResult> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(BridgeStateSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Deletes any persisted state, returning the bridge to a fresh-start condition.</summary>
    Task ResetAsync(CancellationToken cancellationToken);
}
