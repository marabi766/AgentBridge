using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// Recognises, in what an agent prints, that it has run out of allowance — and
/// when it expects to have some again.
///
/// The wordings here are taken from output these tools were actually observed
/// producing, not from what they might plausibly say. The first version of this
/// class guessed, matched none of the three signals Claude Code emits when it
/// refuses, and turned a two-hour wait into a dead run; the tests below quote
/// the real lines so that cannot silently happen again.
///
/// Matching stays deliberately narrow. An agent working on a codebase writes
/// about rate limits all the time, and a loose match would pause a run for hours
/// because the agent mentioned the words in a report.
/// </summary>
public static partial class AgentQuotaSignal
{
    private static readonly string[] Refusals =
    [
        // Claude Code, print mode. "session limit" is the current wording;
        // "usage limit reached" is the older one and still appears.
        "hit your session limit",
        "session limit reached",
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

        if (!IsRefusal(line))
        {
            return false;
        }

        resetsAtUtc = ReadResetTime(line);
        return true;
    }

    private static bool IsRefusal(string line)
    {
        if (Refusals.Any(refusal => line.Contains(refusal, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // The structured signal, and the most reliable of the three: Claude Code
        // emits a rate_limit_event carrying both the verdict and the exact reset.
        // The verdict is required — the same event also announces an allowance
        // that is merely being consumed, and pausing on that would stop a run
        // that has every right to continue.
        if (RateLimitEvent().IsMatch(line) && RejectedStatus().IsMatch(line))
        {
            return true;
        }

        // How the run reports its own ending when the refusal is what stopped it.
        return ApiErrorStatus429().IsMatch(line);
    }

    private static DateTimeOffset? ReadResetTime(string line)
    {
        // The structured event states it outright as "resetsAt": <unix seconds>.
        var field = ResetsAtField().Match(line);
        if (field.Success && TryReadEpoch(field.Groups[1].Value, out var announced))
        {
            return announced;
        }

        // Claude Code's older wording says it as "usage limit reached|<unix seconds>".
        var epoch = EpochAfterPipe().Match(line);
        if (epoch.Success && TryReadEpoch(epoch.Groups[1].Value, out var afterPipe))
        {
            return afterPipe;
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

    private static bool TryReadEpoch(string digits, out DateTimeOffset? value)
    {
        value = null;
        if (!long.TryParse(digits, out var seconds))
        {
            return false;
        }

        // Ten digits is seconds, thirteen is milliseconds; both appear in the
        // wild and reading one as the other is off by fifty thousand years.
        var parsed = digits.Length >= 13
            ? DateTimeOffset.FromUnixTimeMilliseconds(seconds)
            : DateTimeOffset.FromUnixTimeSeconds(seconds);

        // A reset in the past is a stale or misread number, not an instruction to
        // resume immediately. Report that the number was read but say nothing
        // about when, so the caller falls back to its own wait.
        value = parsed > DateTimeOffset.UtcNow ? parsed : null;
        return true;
    }

    [GeneratedRegex(@"""type""\s*:\s*""rate_limit_event""", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitEvent();

    [GeneratedRegex(@"""status""\s*:\s*""rejected""", RegexOptions.IgnoreCase)]
    private static partial Regex RejectedStatus();

    [GeneratedRegex(@"""api_error_status""\s*:\s*429\b")]
    private static partial Regex ApiErrorStatus429();

    [GeneratedRegex(@"""resetsAt""\s*:\s*""?(\d{9,13})""?", RegexOptions.IgnoreCase)]
    private static partial Regex ResetsAtField();

    [GeneratedRegex(@"\|\s*(\d{9,13})\b")]
    private static partial Regex EpochAfterPipe();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z")]
    private static partial Regex IsoTimestamp();
}
