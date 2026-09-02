using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Logging;

public sealed record DailyFileLoggerOptions
{
    public required string LogsDirectory { get; init; }

    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>Safety net: caps how much of a single message body is written, in case a caller
    /// accidentally logs full file/conversation content instead of a hash or summary.</summary>
    public int MaxMessageLength { get; init; } = 4000;
}
