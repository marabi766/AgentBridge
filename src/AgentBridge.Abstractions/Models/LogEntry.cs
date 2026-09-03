using Microsoft.Extensions.Logging;

namespace AgentBridge.Abstractions.Models;

public sealed record LogEntry
{
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Presentation-friendly timestamp in the Windows user's current time zone.
    /// Log files remain canonical UTC on disk.
    /// </summary>
    public DateTimeOffset TimestampLocal => TimestampUtc.ToLocalTime();

    public required LogLevel Level { get; init; }

    public required string Category { get; init; }

    public required string Message { get; init; }

    public string? Exception { get; init; }
}
