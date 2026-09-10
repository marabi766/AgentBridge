using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// An adapter whose agent can hand out a session the operator drives from
/// somewhere else — a phone, another machine.
///
/// Optional, like the other adapter capabilities: an agent that has no such
/// facility simply does not implement it, and the operation is unavailable
/// rather than failing at the moment it is asked for.
/// </summary>
public interface IOpensARemoteControlSession
{
    /// <summary>
    /// Opens a remote control session and waits for the link it announces.
    ///
    /// Returns null, without throwing, when the agent could not be started or
    /// announced no link before the deadline — the caller decides what to say
    /// about it.
    /// </summary>
    Task<RemoteControlSession?> OpenRemoteControlSessionAsync(CancellationToken cancellationToken);
}
