using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Infrastructure.Tests.Persistence;

public sealed class JsonStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abtests-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public JsonStateStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "AgentBridgeState.json");
    }

    private JsonStateStore CreateStore() => new(_path, NullLogger<JsonStateStore>.Instance);

    [Fact]
    public async Task Load_WhenFileDoesNotExist_ReturnsNotFound()
    {
        var store = CreateStore();
        var result = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(StateLoadStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var store = CreateStore();
        var snapshot = new BridgeStateSnapshot
        {
            CurrentState = BridgeState.WaitingForCodex,
            CurrentIteration = 4,
            MaximumIterations = 50,
            LastClaudeReportHash = "abc123",
            LastCodexPromptHash = "def456",
            LastAgent = AgentRole.Claude,
            LastAction = "did a thing",
            LastError = null,
            StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(snapshot, CancellationToken.None);
        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(StateLoadStatus.Loaded, result.Status);
        Assert.Equal(snapshot.CurrentState, result.Snapshot!.CurrentState);
        Assert.Equal(snapshot.CurrentIteration, result.Snapshot.CurrentIteration);
        Assert.Equal(snapshot.LastClaudeReportHash, result.Snapshot.LastClaudeReportHash);
        Assert.Equal(snapshot.LastCodexPromptHash, result.Snapshot.LastCodexPromptHash);
        Assert.Equal(snapshot.LastAgent, result.Snapshot.LastAgent);
        Assert.Equal(snapshot.LastAction, result.Snapshot.LastAction);
    }

    [Fact]
    public async Task Save_OverwritesPreviousContent_AndLeavesNoTempFiles()
    {
        var store = CreateStore();
        await store.SaveAsync(BridgeStateSnapshot.CreateInitial(50, DateTimeOffset.UtcNow), CancellationToken.None);
        await store.SaveAsync(BridgeStateSnapshot.CreateInitial(50, DateTimeOffset.UtcNow) with { CurrentIteration = 9 }, CancellationToken.None);

        var result = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(9, result.Snapshot!.CurrentIteration);

        var leftoverTempFiles = Directory.GetFiles(_dir, "*.tmp");
        Assert.Empty(leftoverTempFiles);
    }

    [Fact]
    public async Task Load_WithCorruptedJson_ReturnsCorrupted_AndBacksUpTheFile()
    {
        await File.WriteAllTextAsync(_path, "{ this is not valid json ");
        var store = CreateStore();

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(StateLoadStatus.Corrupted, result.Status);
        Assert.NotNull(result.BackupFilePath);
        Assert.True(File.Exists(result.BackupFilePath));
    }

    [Fact]
    public async Task Reset_DeletesThePersistedFile()
    {
        var store = CreateStore();
        await store.SaveAsync(BridgeStateSnapshot.CreateInitial(50, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.True(File.Exists(_path));

        await store.ResetAsync(CancellationToken.None);

        Assert.False(File.Exists(_path));
        Assert.Equal(StateLoadStatus.NotFound, (await store.LoadAsync(CancellationToken.None)).Status);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
