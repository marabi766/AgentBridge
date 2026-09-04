using System.Security.Cryptography;
using System.Text;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.FileWatching;

/// <summary>
/// Watches a single file for genuinely new, stable content.
///
/// Pipeline per raw filesystem event: debounce (coalesce a burst of writes) -&gt;
/// poll until N consecutive reads produce the identical SHA-256 hash (stability) -&gt;
/// suppress if that hash was already the last one emitted -&gt; raise
/// <see cref="StableChangeDetected"/> exactly once for genuinely new content.
///
/// A new raw event arriving mid-cycle cancels the in-flight cycle and restarts it —
/// this is what prevents "Claude saved 5 times in 300ms" from producing 5 triggers.
/// </summary>
public sealed class FileWatcherService : IFileWatcher
{
    private readonly FileWatcherOptions _options;
    private readonly ILogger<FileWatcherService> _logger;
    private readonly SemaphoreSlim _emitGate = new(1, 1);
    private readonly Lock _cycleGate = new();

    private FileSystemWatcher? _fsWatcher;
    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _cycleCts;
    private string? _lastEmittedHash;
    private volatile bool _isRunning;

    public FileWatcherService(string filePath, FileWatcherOptions options, ILogger<FileWatcherService> logger)
    {
        FilePath = Path.GetFullPath(filePath);
        _options = options;
        _logger = logger;
    }

    public string FilePath { get; }

    public bool IsRunning => _isRunning;

    public event EventHandler<StableFileChangedEventArgs>? StableChangeDetected;

    public event EventHandler<FileWatcherErrorEventArgs>? Error;

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException($"Cannot determine a parent directory for '{FilePath}'.");
        Directory.CreateDirectory(directory);

        _stopCts = new CancellationTokenSource();

        _fsWatcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _fsWatcher.Changed += OnRawFsEvent;
        _fsWatcher.Created += OnRawFsEvent;
        _fsWatcher.Renamed += OnRawFsEvent;
        _fsWatcher.Error += (_, e) => RaiseError("FileSystemWatcher reported an internal error (e.g. buffer overflow).", e.GetException());
        _fsWatcher.EnableRaisingEvents = true;

        _isRunning = true;
        _logger.LogInformation("Watching {FilePath}", FilePath);
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;

        if (_fsWatcher is not null)
        {
            _fsWatcher.EnableRaisingEvents = false;
            _fsWatcher.Changed -= OnRawFsEvent;
            _fsWatcher.Created -= OnRawFsEvent;
            _fsWatcher.Renamed -= OnRawFsEvent;
            _fsWatcher.Dispose();
            _fsWatcher = null;
        }

        _stopCts?.Cancel();
        _stopCts?.Dispose();
        _stopCts = null;

        lock (_cycleGate)
        {
            _cycleCts?.Cancel();
            _cycleCts = null;
        }

        _logger.LogInformation("Stopped watching {FilePath}", FilePath);
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken)
    {
        var stopToken = _stopCts?.Token;
        using var linked = stopToken is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken.Value);

        try
        {
            var result = await TryReadStableAsync(linked.Token).ConfigureAwait(false);
            if (result.Kind == StableKind.Stable)
            {
                // Bypass this watcher's own last-emitted-hash suppression: CheckNowAsync exists
                // specifically for "tell me the ground truth right now" (startup catch-up, and
                // Resume picking up a change that arrived while paused and was therefore never
                // actually consumed by the orchestrator). The orchestrator's own persisted
                // last-processed-hash is the real authority on whether content is genuinely new —
                // relying on this watcher's transient emission history here would let a change
                // that occurred during a pause go undetected forever.
                await EmitIfNewAsync(result.Content!, result.Hash!, linked.Token, bypassDedup: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Caller-supplied token was cancelled, or Stop() ran concurrently — not an error.
        }
    }

    private void OnRawFsEvent(object sender, FileSystemEventArgs e) => TriggerCycle();

    private void TriggerCycle()
    {
        if (!_isRunning || _stopCts is null)
        {
            return;
        }

        CancellationTokenSource newCts;
        lock (_cycleGate)
        {
            _cycleCts?.Cancel();
            newCts = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token);
            _cycleCts = newCts;
        }

        _ = RunCycleAsync(newCts.Token);
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.DebounceMilliseconds, ct).ConfigureAwait(false);

            var result = await TryReadStableAsync(ct).ConfigureAwait(false);
            if (result.Kind != StableKind.Stable)
            {
                return;
            }

            await EmitIfNewAsync(result.Content!, result.Hash!, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer filesystem event, or the watcher was stopped — expected.
        }
        catch (Exception ex)
        {
            RaiseError("Unexpected error while processing a file change.", ex);
        }
    }

    /// <summary>
    /// Reads the file's own last-write timestamp. A file that cannot be stamped is
    /// reported as written now: the content was just read as stable, so refusing to
    /// date it must not silently make it look older than it is.
    /// </summary>
    private DateTimeOffset ReadLastWriteTimeUtc()
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(FilePath), TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the last write time of {FilePath}.", FilePath);
            return DateTimeOffset.UtcNow;
        }
    }

    private async Task EmitIfNewAsync(string content, string hash, CancellationToken ct, bool bypassDedup = false)
    {
        await _emitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!bypassDedup && string.Equals(hash, _lastEmittedHash, StringComparison.Ordinal))
            {
                _logger.LogDebug("Suppressing duplicate stable hash {Hash} for {FilePath}.", hash, FilePath);
                return;
            }

            _lastEmittedHash = hash;
            StableChangeDetected?.Invoke(this, new StableFileChangedEventArgs
            {
                FilePath = FilePath,
                Content = content,
                ContentHashSha256 = hash,
                LastWriteTimeUtc = ReadLastWriteTimeUtc(),
                DetectedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            _emitGate.Release();
        }
    }

    private enum ReadStatus { NotFound, TransientFailure, Success }

    private readonly record struct ReadOutcome(ReadStatus Status, string? Content, string? Hash);

    private enum StableKind { NotFound, Stable }

    private readonly record struct StableOutcome(StableKind Kind, string? Content, string? Hash);

    private async Task<StableOutcome> TryReadStableAsync(CancellationToken ct)
    {
        string? previousHash = null;
        var consecutive = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var read = await TryReadFileOnceAsync(ct).ConfigureAwait(false);

            if (read.Status == ReadStatus.NotFound)
            {
                return new StableOutcome(StableKind.NotFound, null, null);
            }

            if (read.Status == ReadStatus.TransientFailure)
            {
                consecutive = 0;
                previousHash = null;
            }
            else if (read.Hash == previousHash)
            {
                consecutive++;
            }
            else
            {
                previousHash = read.Hash;
                consecutive = 1;
            }

            if (consecutive >= _options.RequiredConsecutiveStableChecks)
            {
                return new StableOutcome(StableKind.Stable, read.Content, read.Hash);
            }

            await Task.Delay(_options.StabilityCheckIntervalMilliseconds, ct).ConfigureAwait(false);
        }
    }

    private async Task<ReadOutcome> TryReadFileOnceAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
        {
            return new ReadOutcome(ReadStatus.NotFound, null, null);
        }

        for (var attempt = 0; attempt <= _options.ReadRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                var bytes = ms.ToArray();
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var content = Encoding.UTF8.GetString(bytes);
                return new ReadOutcome(ReadStatus.Success, content, hash);
            }
            catch (FileNotFoundException)
            {
                return new ReadOutcome(ReadStatus.NotFound, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= _options.ReadRetryCount)
                {
                    _logger.LogDebug(ex, "Exhausted read retries for {FilePath}.", FilePath);
                    break;
                }

                await Task.Delay(_options.ReadRetryDelayMilliseconds, ct).ConfigureAwait(false);
            }
        }

        return new ReadOutcome(ReadStatus.TransientFailure, null, null);
    }

    private void RaiseError(string message, Exception? ex)
    {
        _logger.LogWarning(ex, "{Message} ({FilePath})", message, FilePath);
        Error?.Invoke(this, new FileWatcherErrorEventArgs
        {
            FilePath = FilePath,
            Message = message,
            Exception = ex,
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });
    }

    public void Dispose()
    {
        Stop();
        _emitGate.Dispose();
    }
}
