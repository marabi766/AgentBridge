using Microsoft.Extensions.Logging;

namespace AgentBridge.Abstractions.Models;

public sealed record LogEntry
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required LogLevel Level { get; init; }

    public required string Category { get; init; }

    public required string Message { get; init; }

    public string? Exception { get; init; }
}
