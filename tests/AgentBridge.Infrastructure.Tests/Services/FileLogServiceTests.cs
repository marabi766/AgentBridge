using System.Globalization;
using AgentBridge.Infrastructure.Services;

namespace AgentBridge.Infrastructure.Tests.Services;

public sealed class FileLogServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentBridge-FileLogTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TailAsync_CanReadLogWhileLoggerKeepsFileOpenForWriting()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        await using var writerStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(writerStream) { AutoFlush = true };
        await writer.WriteLineAsync("[2026-09-02T10:00:00.000Z] [Information] [Test] visible while writer is open");

        var service = new FileLogService(_directory);
        var entries = await service.TailAsync(10, CancellationToken.None);

        Assert.Single(entries);
        Assert.Equal("visible while writer is open", entries[0].Message);
    }

    [Fact]
    public async Task ExportAsync_WritesEveryDayOldestFirstIncludingTheOneStillBeingWritten()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "2026-09-01.log"),
            "[2026-09-01T10:00:00.000Z] [Information] [Test] the older day" + Environment.NewLine);

        // The current day is held open by the logger. An export that skipped it
        // would omit exactly the part anyone exporting a log wants.
        var today = Path.Combine(_directory, "2026-09-02.log");
        await using var open = new FileStream(today, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(open) { AutoFlush = true };
        await writer.WriteLineAsync("[2026-09-02T10:00:00.000Z] [Information] [Test] the day in use");

        var destination = Path.Combine(_directory, "export.txt");
        var lines = await new FileLogService(_directory).ExportAsync(destination, CancellationToken.None);

        var text = await File.ReadAllTextAsync(destination);
        Assert.Equal(2, lines);
        Assert.Contains("the older day", text, StringComparison.Ordinal);
        Assert.Contains("the day in use", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("the older day", StringComparison.Ordinal)
            < text.IndexOf("the day in use", StringComparison.Ordinal),
            "Days must be exported oldest first.");
    }

    [Fact]
    public async Task ClearAsync_ReportsTheFileItCouldNotDeleteRatherThanClaimingSuccess()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "2026-09-01.log"), "old" + Environment.NewLine);

        var today = Path.Combine(_directory, "2026-09-02.log");
        await using var open = new FileStream(today, FileMode.Append, FileAccess.Write, FileShare.Read);

        var result = await new FileLogService(_directory).ClearAsync(CancellationToken.None);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(["2026-09-02.log"], result.FilesInUse);
        Assert.True(File.Exists(today), "The open file must survive so the report is truthful.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
