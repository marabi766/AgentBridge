using System.Globalization;
using AgentBridge.Infrastructure.Services;

namespace AgentBridge.Infrastructure.Tests.Services;

/// <summary>
/// Reading the log forward rather than re-reading it. The cost of re-reading
/// grows with the run, which is exactly backwards: the longer an agent works,
/// the more it matters that watching it stays cheap.
/// </summary>
public sealed class IncrementalLogReadTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "AgentBridge-IncrementalLog", Guid.NewGuid().ToString("N"));

    private string TodayPath => Path.Combine(
        _directory, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    private static string Line(string message) =>
        $"[2026-09-09T10:00:00.000Z] [Information] [Test] {message}";

    [Fact]
    public async Task ANegativePositionStartsAtTheEndRatherThanReplayingTheDay()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllLinesAsync(TodayPath, [Line("one"), Line("two")]);

        var result = await new FileLogService(_directory).ReadSinceAsync(-1, 250, CancellationToken.None);

        Assert.True(result.Restarted);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(new FileInfo(TodayPath).Length, result.Position);
    }

    [Fact]
    public async Task OnlyWhatArrivedSinceTheLastLookIsReturned()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllLinesAsync(TodayPath, [Line("already seen")]);

        var service = new FileLogService(_directory);
        var first = await service.ReadSinceAsync(-1, 250, CancellationToken.None);

        await File.AppendAllLinesAsync(TodayPath, [Line("brand new")]);
        var second = await service.ReadSinceAsync(first.Position, 250, CancellationToken.None);

        Assert.False(second.Restarted);
        Assert.Single(second.Entries);
        Assert.Equal("brand new", second.Entries[0].Message);
    }

    [Fact]
    public async Task NothingNewMeansNothingReturnedAndThePositionHolds()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllLinesAsync(TodayPath, [Line("one")]);

        var service = new FileLogService(_directory);
        var first = await service.ReadSinceAsync(-1, 250, CancellationToken.None);
        var second = await service.ReadSinceAsync(first.Position, 250, CancellationToken.None);

        Assert.Empty(second.Entries);
        Assert.False(second.Restarted);
        Assert.Equal(first.Position, second.Position);
    }

    [Fact]
    public async Task AHalfWrittenLineIsLeftUntilItIsWhole()
    {
        // The logger appends while this reads. Returning a line the writer has
        // not finished would show a truncated entry once and never correct it,
        // because the position would have moved past it.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(TodayPath, Line("complete") + Environment.NewLine);

        var service = new FileLogService(_directory);
        var first = await service.ReadSinceAsync(-1, 250, CancellationToken.None);

        await File.AppendAllTextAsync(TodayPath, "[2026-09-09T10:00:01.000Z] [Information] [Test] half a li");
        var partial = await service.ReadSinceAsync(first.Position, 250, CancellationToken.None);

        Assert.Empty(partial.Entries);
        Assert.Equal(first.Position, partial.Position);

        // Once the writer finishes the line, it is read whole.
        await File.AppendAllTextAsync(TodayPath, "ne" + Environment.NewLine);
        var complete = await service.ReadSinceAsync(partial.Position, 250, CancellationToken.None);

        Assert.Single(complete.Entries);
        Assert.Equal("half a line", complete.Entries[0].Message);
    }

    [Fact]
    public async Task AFileTruncatedUnderneathTheReaderRestartsRatherThanReadingRubbish()
    {
        // A position past the end can only mean the file was replaced or cut.
        // Reading from it would seek beyond the data; saying so lets the caller
        // replace what it shows instead of appending to an unrelated stretch.
        Directory.CreateDirectory(_directory);
        await File.WriteAllLinesAsync(TodayPath, [Line("one"), Line("two"), Line("three")]);

        var service = new FileLogService(_directory);
        var beforeTruncation = await service.ReadSinceAsync(-1, 250, CancellationToken.None);

        await File.WriteAllLinesAsync(TodayPath, [Line("fresh")]);
        var after = await service.ReadSinceAsync(beforeTruncation.Position, 250, CancellationToken.None);

        Assert.True(after.Restarted);
        Assert.Single(after.Entries);
        Assert.Equal("fresh", after.Entries[0].Message);
    }

    [Fact]
    public async Task ReadingIsPossibleWhileTheLoggerHoldsTheFileOpen()
    {
        Directory.CreateDirectory(_directory);
        await using var open = new FileStream(TodayPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(open) { AutoFlush = true };
        await writer.WriteLineAsync(Line("written while open"));

        var result = await new FileLogService(_directory).ReadSinceAsync(-1, 250, CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Equal("written while open", result.Entries[0].Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
