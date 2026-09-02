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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
