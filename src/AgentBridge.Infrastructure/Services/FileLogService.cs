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


    /// <summary>
    /// Reads forward from a byte offset in the current day's log.
    ///
    /// A byte offset rather than a line count because the file is appended to
    /// while it is read: counting lines would mean re-reading everything before
    /// them to know where they start, which is the cost this exists to avoid.
    /// </summary>
    public async Task<LogTailResult> ReadSinceAsync(long position, int maxEntries, CancellationToken cancellationToken)
    {
        var dates = await GetAvailableLogDatesAsync(cancellationToken).ConfigureAwait(false);
        if (dates.Count == 0)
        {
            return new LogTailResult { Entries = [], Position = 0, Restarted = position >= 0 };
        }

        var path = Path.Combine(logsDirectory, dates[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        if (!File.Exists(path))
        {
            return new LogTailResult { Entries = [], Position = 0, Restarted = position >= 0 };
        }

        // FileShare.ReadWrite because the logger owns this file and keeps it open.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Rolled over to a new day, or truncated underneath us. Either way the
        // offset means nothing now, so start again from the end and say so.
        var restarted = position < 0 || position > stream.Length;
        if (restarted)
        {
            var tail = await TailAsync(maxEntries, cancellationToken).ConfigureAwait(false);
            return new LogTailResult { Entries = tail, Position = stream.Length, Restarted = true };
        }

        if (position == stream.Length)
        {
            return new LogTailResult { Entries = [], Position = position, Restarted = false };
        }

        stream.Seek(position, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - position];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        // The writer may be mid-line. Stopping at the last newline keeps a half
        // written entry out of the view and leaves it to be read whole next time.
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNewline < 0)
        {
            return new LogTailResult { Entries = [], Position = position, Restarted = false };
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();

        var entries = ParseLines(lines);
        if (entries.Count > maxEntries)
        {
            entries = entries.Skip(entries.Count - maxEntries).ToList();
        }

        return new LogTailResult
        {
            Entries = entries,
            Position = position + lastNewline + 1,
            Restarted = false,
        };
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

    /// <summary>
    /// Concatenates every stored log into one file, oldest day first, each line
    /// prefixed with the day it came from so the joined file is still readable.
    /// Reading uses the same sharing mode as the tail so today's log, which the
    /// writer holds open, is included rather than skipped.
    /// </summary>
    public async Task<int> ExportAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var written = 0;
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var output = new StreamWriter(destinationPath, append: false);
        await output.WriteLineAsync(
            $"# Agent Bridge log export, written {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}").ConfigureAwait(false);

        if (!Directory.Exists(logsDirectory))
        {
            return written;
        }

        foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log").OrderBy(f => f, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync($"# ---- {Path.GetFileName(file)} ----").ConfigureAwait(false);

            // FileShare.ReadWrite because the writer still owns today's file.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                await output.WriteLineAsync(line).ConfigureAwait(false);
                written++;
            }
        }

        return written;
    }
}
