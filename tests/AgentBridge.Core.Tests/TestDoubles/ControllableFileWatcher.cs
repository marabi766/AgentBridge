using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

/// <summary>
/// A file watcher double a test drives directly — no real file I/O. Raising
/// <see cref="RaiseStableChange"/> simulates "the watcher decided this content is
/// stable and new"; the test controls exactly when and with what hash.
/// </summary>
public sealed class ControllableFileWatcher(string filePath) : IFileWatcher
{
    private string? _pendingContent;
    private string? _pendingHash;

    public string FilePath { get; } = filePath;

    public bool IsRunning { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int CheckNowCallCount { get; private set; }

    public event EventHandler<StableFileChangedEventArgs>? StableChangeDetected;

    public event EventHandler<FileWatcherErrorEventArgs>? Error;

    public void Start()
    {
        IsRunning = true;
        StartCount++;
    }

    public void Stop()
    {
        IsRunning = false;
        StopCount++;
    }

    /// <summary>Arranges the content CheckNowAsync will "discover" the next time it's called.</summary>
    public void ArrangeCheckNowResult(string content, string hash)
    {
        _pendingContent = content;
        _pendingHash = hash;
    }

    public Task CheckNowAsync(CancellationToken cancellationToken)
    {
        CheckNowCallCount++;
        if (_pendingContent is not null && _pendingHash is not null)
        {
            RaiseStableChange(_pendingContent, _pendingHash);
            _pendingContent = null;
            _pendingHash = null;
        }

        return Task.CompletedTask;
    }

    public void RaiseStableChange(string content, string hash) =>
        StableChangeDetected?.Invoke(this, new StableFileChangedEventArgs
        {
            FilePath = FilePath,
            Content = content,
            ContentHashSha256 = hash,
            DetectedAtUtc = DateTimeOffset.UtcNow,
        });

    public void RaiseError(string message, Exception? ex = null) =>
        Error?.Invoke(this, new FileWatcherErrorEventArgs
        {
            FilePath = FilePath,
            Message = message,
            Exception = ex,
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });

    public void Dispose()
    {
    }
}

public sealed class ControllableFileWatcherFactory : IFileWatcherFactory
{
    private readonly Dictionary<string, ControllableFileWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    public IFileWatcher Create(string filePath)
    {
        var watcher = new ControllableFileWatcher(filePath);
        _watchers[filePath] = watcher;
        return watcher;
    }

    public ControllableFileWatcher Get(string filePath) => _watchers[filePath];

    public bool WasCreated(string filePath) => _watchers.ContainsKey(filePath);
}
