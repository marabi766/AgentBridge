namespace AgentBridge.Abstractions.Models;

/// <summary>
/// What has been appended to the log since the caller last looked, and where to
/// resume from next time.
///
/// Re-reading the whole file every few seconds costs more the longer a run goes
/// on — exactly when the reading matters most — and hands back the same hundreds
/// of entries the caller already has. Reading forward from a position costs only
/// what actually arrived.
/// </summary>
public sealed record LogTailResult
{
    public required IReadOnlyList<LogEntry> Entries { get; init; }

    /// <summary>Where to resume. Opaque to the caller; only ever passed back.</summary>
    public required long Position { get; init; }

    /// <summary>
    /// True when reading could not continue from where the caller left off — a
    /// new day's file, or one truncated underneath us — so <see cref="Entries"/>
    /// is a fresh tail rather than a continuation. The caller must replace what
    /// it is showing rather than appending to it, or it would show two unrelated
    /// stretches of time as one.
    /// </summary>
    public required bool Restarted { get; init; }
}
