namespace AgentBridge.Abstractions.Models;

/// <summary>
/// A live agent session that can be driven from another device.
///
/// The session is a copy of the conversation the bridge was last having with
/// that agent, not the bridge's own run: the bridge drives its agents through
/// one-shot processes that exit, so there is no long-lived session to hand over.
/// Copying means the operator sees everything the agent had done and can steer
/// it from a phone without anything they type reaching back into a run the
/// bridge is still watching.
/// </summary>
public sealed record RemoteControlSession
{
    /// <summary>The short id the CLI's own commands take (attach, logs, stop).</summary>
    public required string SessionId { get; init; }

    /// <summary>The link to open on the other device.</summary>
    public required string Url { get; init; }

    public required DateTimeOffset OpenedAtUtc { get; init; }
}
