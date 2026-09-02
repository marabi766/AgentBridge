using AgentBridge.Infrastructure.Logging;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Tests.Logging;

public sealed class DailyFileLoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abtests-logs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoggedMessages_AreWrittenToTodaysFile_AndReadableBackViaFileLogService()
    {
        using (var provider = new DailyFileLoggerProvider(new DailyFileLoggerOptions { LogsDirectory = _dir }))
        {
            var logger = provider.CreateLogger("MyCategory");
            logger.LogInformation("Hello {Name}", "World");
            logger.LogWarning("Careful now");

            // The provider funnels writes through a background channel — give it a moment.
            await Task.Delay(300);
        } // Dispose flushes and closes the writer.

        var logService = new FileLogService(_dir);
        var dates = await logService.GetAvailableLogDatesAsync(CancellationToken.None);
        Assert.Single(dates);

        var entries = await logService.ReadLogAsync(dates[0], CancellationToken.None);
        Assert.Contains(entries, e => e.Message.Contains("Hello World") && e.Level == LogLevel.Information);
        Assert.Contains(entries, e => e.Message.Contains("Careful now") && e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task MinimumLevel_FiltersOutLowerSeverityMessages()
    {
        using (var provider = new DailyFileLoggerProvider(new DailyFileLoggerOptions
        {
            LogsDirectory = _dir,
            MinimumLevel = LogLevel.Warning,
        }))
        {
            var logger = provider.CreateLogger("Cat");
            logger.LogInformation("should be filtered out");
            logger.LogError("should appear");
            await Task.Delay(300);
        }

        var logService = new FileLogService(_dir);
        var dates = await logService.GetAvailableLogDatesAsync(CancellationToken.None);
        var entries = await logService.ReadLogAsync(dates[0], CancellationToken.None);

        Assert.DoesNotContain(entries, e => e.Message.Contains("should be filtered out"));
        Assert.Contains(entries, e => e.Message.Contains("should appear"));
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
