using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AgentBridge.Infrastructure.Logging;

/// <summary>
/// Writes log records to one file per calendar day (UTC) under
/// <see cref="DailyFileLoggerOptions.LogsDirectory"/>, e.g. logs/2026-09-02.log.
/// Writes are funneled through a single background task via a channel so
/// concurrent loggers never interleave partial lines or contend on file I/O.
/// </summary>
[ProviderAlias("DailyFile")]
public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly DailyFileLoggerOptions _options;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _shutdownCts = new();

    public DailyFileLoggerProvider(DailyFileLoggerOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.LogsDirectory);
        _writerTask = Task.Run(() => WriteLoopAsync(_shutdownCts.Token));
    }

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(categoryName, this);

    internal void Enqueue(string line) => _channel.Writer.TryWrite(line);

    internal DailyFileLoggerOptions Options => _options;

    private async Task WriteLoopAsync(CancellationToken shutdownToken)
    {
        string? currentDate = null;
        StreamWriter? writer = null;

        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (writer is null || today != currentDate)
                {
                    writer?.Dispose();
                    var path = Path.Combine(_options.LogsDirectory, $"{today}.log");
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true,
                    };
                    currentDate = today;
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            writer?.Dispose();
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort flush on shutdown — never let logging teardown crash the app.
        }

        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }
}
