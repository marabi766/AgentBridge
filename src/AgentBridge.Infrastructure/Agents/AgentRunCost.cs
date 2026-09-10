using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// What one run reported it spent.
///
/// The numbers are already in the output — the CLI prints them on the line that
/// ends a run — but buried in JSON among thirty other fields, where nobody reads
/// them. An operator watching an allowance drain has no way to see which runs
/// cost what, and the answer turns out to be very uneven: runs measured here
/// ranged from a few cents to nearly five dollars, and the expensive ones were
/// expensive because of how many turns they took, not how much they wrote.
/// </summary>
public sealed record AgentRunCost
{
    public decimal? UsdCost { get; init; }

    public int? Turns { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    /// <summary>Context read back from cache — cheap, and usually most of the total.</summary>
    public long? CacheReadTokens { get; init; }

    public long? CacheWriteTokens { get; init; }

    /// <summary>A sentence an operator can read in a log without decoding JSON.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (UsdCost is { } cost)
        {
            parts.Add(cost.ToString("C2", CultureInfo.GetCultureInfo("en-US")));
        }

        if (Turns is { } turns)
        {
            parts.Add($"{turns} turns");
        }

        if (OutputTokens is { } output)
        {
            parts.Add($"{output:N0} out");
        }

        // Reported separately because they are priced very differently, and an
        // operator deciding whether to reuse sessions needs to see which is
        // which: cache reads are what resuming a session buys.
        if (CacheReadTokens is { } cacheRead && cacheRead > 0)
        {
            parts.Add($"{cacheRead:N0} cached in");
        }

        if (InputTokens is { } input && input > 0)
        {
            parts.Add($"{input:N0} fresh in");
        }

        return parts.Count == 0 ? "no usage reported" : string.Join(" · ", parts);
    }
}

/// <summary>
/// Reads the usage a run reported. Like the other output readers here, the
/// fields are the ones actually observed rather than a guess at the schema.
/// </summary>
public static partial class AgentRunCostReader
{
    /// <summary>
    /// Returns what this line says a run spent, or null when it says nothing
    /// about usage. Every field is optional on purpose: the CLI has moved fields
    /// between shapes before, and a partial reading beats none.
    /// </summary>
    public static AgentRunCost? TryRead(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"total_cost_usd\"", StringComparison.Ordinal))
        {
            return null;
        }

        return new AgentRunCost
        {
            UsdCost = Decimal(line, Cost()),
            Turns = (int?)Integer(line, Turns()),
            InputTokens = Integer(line, InputTokens()),
            OutputTokens = Integer(line, OutputTokens()),
            CacheReadTokens = Integer(line, CacheRead()),
            CacheWriteTokens = Integer(line, CacheWrite()),
        };
    }

    private static decimal? Decimal(string line, Regex pattern)
    {
        var match = pattern.Match(line);
        return match.Success
            && decimal.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static long? Integer(string line, Regex pattern)
    {
        var match = pattern.Match(line);
        return match.Success && long.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    [GeneratedRegex(@"""total_cost_usd""\s*:\s*([0-9.]+)")]
    private static partial Regex Cost();

    [GeneratedRegex(@"""num_turns""\s*:\s*(\d+)")]
    private static partial Regex Turns();

    // Anchored to the exact name: "input_tokens" is also the tail of
    // "cache_read_input_tokens", and matching that would report cached context
    // as freshly paid for — the opposite of what the number is for.
    [GeneratedRegex(@"(?<![_a-z])""input_tokens""\s*:\s*(\d+)")]
    private static partial Regex InputTokens();

    [GeneratedRegex(@"(?<![_a-z])""output_tokens""\s*:\s*(\d+)")]
    private static partial Regex OutputTokens();

    [GeneratedRegex(@"""cache_read_input_tokens""\s*:\s*(\d+)")]
    private static partial Regex CacheRead();

    [GeneratedRegex(@"""cache_creation_input_tokens""\s*:\s*(\d+)")]
    private static partial Regex CacheWrite();
}
