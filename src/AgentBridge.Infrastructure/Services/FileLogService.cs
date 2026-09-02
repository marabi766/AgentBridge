using System.Text.RegularExpressions;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AgentBridge.Infrastructure.Services;

public sealed partial class FileLogService(string logsDirectory) : ILogService
{
    [GeneratedRegex(@"^\[(?<ts>[^\]]+)\]\s\[(?<level>[^\]]+)\]\s\[(?<category>[^\]]+)\]\s(?<message>.*)$")]
    private static partial Regex LinePattern();

    public Task<IReadOnlyList<DateOnly>> GetAvailableLogDatesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return Task.FromResult<IReadOnlyList<DateOnly>>(Array.Empty<DateOnly>());
        }

        var dates = Directory.EnumerateFiles(logsDirectory, "*.log")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? (DateOnly?)d : null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .OrderByDescending(d => d)
            .ToList();

        return Task.FromResult<IReadOnlyList<DateOnly>>(dates);
    }

    public async Task<IReadOnlyList<LogEntry>> ReadLogAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var path = Path.Combine(logsDirectory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        if (!File.Exists(path))
        {
            return Array.Empty<LogEntry>();
        }

        var lines = new List<string>();
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
        }
        return ParseLines(lines);
    }

    public async Task<IReadOnlyList<LogEntry>> TailAsync(int maxEntries, CancellationToken cancellationToken)
    {
        var dates = await GetAvailableLogDatesAsync(cancellationToken).ConfigureAwait(false);
        if (dates.Count == 0)
        {
            return Array.Empty<LogEntry>();
        }

        var entries = await ReadLogAsync(dates[0], cancellationToken).ConfigureAwait(false);
        return entries.Count <= maxEntries ? entries : entries.Skip(entries.Count - maxEntries).ToList();
    }

    private static IReadOnlyList<LogEntry> ParseLines(IReadOnlyList<string> lines)
    {
        var result = new List<LogEntry>();
        foreach (var line in lines)
        {
            var match = LinePattern().Match(line);
            if (match.Success)
            {
                var level = Enum.TryParse<LogLevel>(match.Groups["level"].Value, out var lvl) ? lvl : LogLevel.None;
                var ts = DateTimeOffset.TryParse(match.Groups["ts"].Value, out var parsedTs) ? parsedTs : DateTimeOffset.MinValue;
                result.Add(new LogEntry
                {
                    TimestampUtc = ts,
                    Level = level,
                    Category = match.Groups["category"].Value,
                    Message = match.Groups["message"].Value,
                });
            }
            else if (result.Count > 0)
            {
                var prev = result[^1];
                result[^1] = prev with
                {
                    Exception = prev.Exception is null ? line : prev.Exception + Environment.NewLine + line,
                };
            }
        }

        return result;
    }
}
