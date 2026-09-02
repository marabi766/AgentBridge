using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.FileWatching;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Infrastructure.Tests.FileWatching;

public sealed class FileWatcherServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abtests-" + Guid.NewGuid().ToString("N"));
    private readonly List<AgentBridge.Abstractions.Interfaces.IFileWatcher> _watchers = [];

    public FileWatcherServiceTests() => Directory.CreateDirectory(_dir);

    private FileWatcherService CreateWatcher(string fileName, FileWatcherOptions? options = null)
    {
        var watcher = new FileWatcherService(
            Path.Combine(_dir, fileName),
            options ?? new FileWatcherOptions
            {
                DebounceMilliseconds = 60,
                StabilityCheckIntervalMilliseconds = 40,
                RequiredConsecutiveStableChecks = 2,
                ReadRetryCount = 5,
                ReadRetryDelayMilliseconds = 50,
            },
            NullLogger<FileWatcherService>.Instance);
        _watchers.Add(watcher);
        return watcher;
    }

    private static async Task<StableFileChangedEventArgs> WaitForEventAsync(
        AgentBridge.Abstractions.Interfaces.IFileWatcher watcher, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<StableFileChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, StableFileChangedEventArgs e) => tcs.TrySetResult(e);
        watcher.StableChangeDetected += Handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
            if (completed != tcs.Task)
            {
                throw new TimeoutException("Timed out waiting for StableChangeDetected.");
            }

            return await tcs.Task;
        }
        finally
        {
            watcher.StableChangeDetected -= Handler;
        }
    }

    [Fact]
    public async Task SingleStableWrite_RaisesEventWithCorrectHash()
    {
        var path = Path.Combine(_dir, "a.md");
        var watcher = CreateWatcher("a.md");
        watcher.Start();

        await File.WriteAllTextAsync(path, "hello world");

        var e = await WaitForEventAsync(watcher);
        Assert.Equal("hello world", e.Content);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("hello world"))), e.ContentHashSha256);
    }

    [Fact]
    public async Task RapidSuccessiveSaves_ProduceExactlyOneEvent_ForTheFinalContent()
    {
        var path = Path.Combine(_dir, "b.md");
        var watcher = CreateWatcher("b.md");
        var events = new List<StableFileChangedEventArgs>();
        watcher.StableChangeDetected += (_, e) => events.Add(e);
        watcher.Start();

        for (var i = 1; i <= 5; i++)
        {
            await File.WriteAllTextAsync(path, $"version {i}");
            await Task.Delay(20); // faster than the 60ms debounce — simulates a burst of saves
        }

        // Give the final debounce+stability cycle time to settle.
        await Task.Delay(1000);

        Assert.Single(events);
        Assert.Equal("version 5", events[0].Content);
    }

    [Fact]
    public async Task IdenticalContentRewritten_DoesNotRaiseASecondEvent()
    {
        var path = Path.Combine(_dir, "c.md");
        var watcher = CreateWatcher("c.md");
        var events = new List<StableFileChangedEventArgs>();
        watcher.StableChangeDetected += (_, e) => events.Add(e);
        watcher.Start();

        await File.WriteAllTextAsync(path, "same content");
        await WaitForEventAsync(watcher);

        await File.WriteAllTextAsync(path, "same content"); // re-save, byte-identical
        await Task.Delay(500);

        Assert.Single(events); // the second write produced the same hash — suppressed
    }

    [Fact]
    public async Task FileRecreatedAfterDeletion_RaisesANewEvent()
    {
        var path = Path.Combine(_dir, "d.md");
        var watcher = CreateWatcher("d.md");
        watcher.Start();

        await File.WriteAllTextAsync(path, "first");
        await WaitForEventAsync(watcher);

        File.Delete(path);
        await Task.Delay(150);
        await File.WriteAllTextAsync(path, "second");

        var e = await WaitForEventAsync(watcher);
        Assert.Equal("second", e.Content);
    }

    [Fact]
    public async Task MissingFile_NeverThrows_AndToleratesAbsence()
    {
        var watcher = CreateWatcher("never-created.md");
        watcher.Start();

        await Task.Delay(300); // nothing should happen, and nothing should throw
        await watcher.CheckNowAsync(CancellationToken.None); // also tolerant on-demand
    }

    [Fact]
    public async Task CheckNowAsync_PicksUpAPreExistingFile_WithoutAnFsEvent()
    {
        var path = Path.Combine(_dir, "e.md");
        await File.WriteAllTextAsync(path, "already here before Start");

        var watcher = CreateWatcher("e.md");
        // Deliberately do not Start() the FileSystemWatcher — CheckNowAsync must work standalone.
        var events = new List<StableFileChangedEventArgs>();
        watcher.StableChangeDetected += (_, e) => events.Add(e);

        await watcher.CheckNowAsync(CancellationToken.None);

        Assert.Single(events);
        Assert.Equal("already here before Start", events[0].Content);
    }

    [Fact]
    public async Task CheckNowAsync_AlwaysReportsGroundTruth_EvenIfIdenticalToAPriorCheck()
    {
        // CheckNowAsync exists for catch-up scenarios (startup, Resume-after-Pause) where a
        // change may have arrived while nobody was listening and was therefore never actually
        // consumed. It must not go silent just because it already reported this exact content
        // once before — the caller's own last-processed-hash is what decides "already handled".
        var path = Path.Combine(_dir, "g.md");
        await File.WriteAllTextAsync(path, "unchanged content");

        var watcher = CreateWatcher("g.md");
        await watcher.CheckNowAsync(CancellationToken.None);

        var events = new List<StableFileChangedEventArgs>();
        watcher.StableChangeDetected += (_, e) => events.Add(e);
        await watcher.CheckNowAsync(CancellationToken.None);

        Assert.Single(events);
    }

    [Fact]
    public async Task LockedFileDuringWrite_IsRetriedAndEventuallyDetected()
    {
        var path = Path.Combine(_dir, "f.md");
        var watcher = CreateWatcher("f.md", new FileWatcherOptions
        {
            DebounceMilliseconds = 30,
            StabilityCheckIntervalMilliseconds = 40,
            RequiredConsecutiveStableChecks = 2,
            ReadRetryCount = 10,
            ReadRetryDelayMilliseconds = 40,
        });
        watcher.Start();

        // Hold an exclusive lock briefly to simulate "another process still has it open",
        // well within the configured read-retry budget (10 retries * 40ms = 400ms).
        await using (var locked = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("locked content");
            await locked.WriteAsync(bytes);
            await locked.FlushAsync();
            await Task.Delay(150);
        }

        var e = await WaitForEventAsync(watcher, TimeSpan.FromSeconds(5));
        Assert.Equal("locked content", e.Content);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup — a lingering temp dir is not worth failing the test run over.
        }
    }
}
