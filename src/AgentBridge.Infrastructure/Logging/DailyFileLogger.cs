using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Logging;

internal sealed class DailyFileLogger(string categoryName, DailyFileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.Options.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var maxLen = provider.Options.MaxMessageLength;
        if (message.Length > maxLen)
        {
            message = message[..maxLen] + "...[truncated]";
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var line = $"[{timestamp}] [{logLevel}] [{categoryName}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        provider.Enqueue(line);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
