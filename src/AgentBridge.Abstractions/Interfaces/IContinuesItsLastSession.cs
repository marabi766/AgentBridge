namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// An adapter that can send a message into the conversation its agent was
/// already having, rather than starting a fresh one.
///
/// This exists because the two routes differ on exactly this point. A desktop
/// window <em>is</em> the conversation: typing "continue" into it needs nothing
/// special. A command line run is a new process with no memory of the last one,
/// so "continue" would arrive at an agent that has no idea what it is being
/// asked to continue — the word is only meaningful if the session comes back
/// with it.
/// </summary>
public interface IContinuesItsLastSession
{
    /// <summary>
    /// Sends <paramref name="message"/> into the agent's most recent session.
    /// Returns false, without throwing, when there is no session to resume or the
    /// agent could not be started — the caller decides what to do about it.
    /// </summary>
    Task<bool> ContinueLastSessionAsync(string message, CancellationToken cancellationToken);
}
