using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// Watches a single file for genuinely new, stable content. Implementations own
/// debouncing, stability polling, duplicate-event suppression and hashing — callers
/// only ever see <see cref="StableChangeDetected"/> for content that is both
/// settled on disk AND different (by hash) from the last content this watcher emitted.
/// </summary>
public interface IFileWatcher : IDisposable
{
    string FilePath { get; }

    bool IsRunning { get; }

    event EventHandler<StableFileChangedEventArgs>? StableChangeDetected;

    event EventHandler<FileWatcherErrorEventArgs>? Error;

    void Start();

    void Stop();

    /// <summary>
    /// Forces an immediate stability check + read of the current file content,
    /// bypassing the wait for a filesystem event. Used on startup and on Resume to
    /// pick up changes that happened while the bridge was not running or was
    /// paused, and by diagnostics. Reports the ground truth of what is currently
    /// stable on disk even if this exact hash was already emitted before — a
    /// change that arrives while the orchestrator ignores events (paused, wrong
    /// state) is never actually consumed, so this call must not stay silent about
    /// it. Callers rely on their own persisted last-processed-hash, not this
    /// watcher's emission history, to decide whether content is genuinely new.
    /// </summary>
    Task CheckNowAsync(CancellationToken cancellationToken);
}
