using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// Recognises, in what an agent prints, that it has run out of allowance — and
/// when it expects to have some again.
///
/// The phrases are deliberately specific rather than generous. An agent working
/// on a codebase writes about rate limits and quotas all the time, and a loose
/// match would pause a run for hours because the agent mentioned the words in a
/// report. Matching only the wordings the tools actually emit when they refuse
/// is the difference between a useful pause and an invented one.
/// </summary>
public static partial class AgentQuotaSignal
{
    private static readonly string[] Refusals =
    [
        // Claude Code, print mode.
        "usage limit reached",
        // Anthropic and OpenAI API error bodies, which both CLIs surface verbatim.
        "rate_limit_error",
        "rate limit exceeded",
        "quota exceeded",
        "insufficient_quota",
    ];

    /// <summary>
    /// True when this line says the agent has no allowance left.
    /// <paramref name="resetsAtUtc"/> is the moment it expects to have some
    /// again, or null when the line does not say — the caller decides how long
    /// to wait in that case rather than guessing here.
    /// </summary>
    public static bool TryDetect(string line, out DateTimeOffset? resetsAtUtc)
    {
        resetsAtUtc = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (!Refusals.Any(refusal => line.Contains(refusal, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        resetsAtUtc = ReadResetTime(line);
        return true;
    }

    private static DateTimeOffset? ReadResetTime(string line)
    {
        // Claude Code says it as "usage limit reached|<unix seconds>".
        var epoch = EpochAfterPipe().Match(line);
        if (epoch.Success && long.TryParse(epoch.Groups[1].Value, out var seconds))
        {
            // Ten digits is seconds, thirteen is milliseconds; both appear in the
            // wild and reading one as the other is off by fifty thousand years.
            var value = epoch.Groups[1].Value.Length >= 13
                ? DateTimeOffset.FromUnixTimeMilliseconds(seconds)
                : DateTimeOffset.FromUnixTimeSeconds(seconds);

            // A reset in the past is a stale or misread number, not an instruction
            // to resume immediately.
            return value > DateTimeOffset.UtcNow ? value : null;
        }

        var timestamp = IsoTimestamp().Match(line);
        if (timestamp.Success
            && DateTimeOffset.TryParse(
                timestamp.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            && parsed > DateTimeOffset.UtcNow)
        {
            return parsed;
        }

        return null;
    }

    [GeneratedRegex(@"\|\s*(\d{9,13})\b")]
    private static partial Regex EpochAfterPipe();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z")]
    private static partial Regex IsoTimestamp();
}
