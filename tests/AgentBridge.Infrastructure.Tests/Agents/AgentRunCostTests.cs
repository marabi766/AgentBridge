using AgentBridge.Infrastructure.Agents;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

/// <summary>
/// The line below is quoted from the run of 2026-09-09 that cost $4.71. Its
/// numbers are the whole reason this reader exists: five and a half million
/// tokens of it were cache reads, ninety-six were freshly paid input, and none
/// of that was visible to anyone watching an allowance drain.
/// </summary>
public sealed class AgentRunCostTests
{
    private const string RealResultLine =
        """{"duration_api_ms":508952,"stop_reason":"stop_sequence","session_id":"3e80955f","total_cost_usd":4.713679000000001,"num_turns":65,"usage":{"input_tokens":96,"cache_creation_input_tokens":121263,"cache_read_input_tokens":5486166,"output_tokens":30252}}""";

    [Fact]
    public void EveryFigureIsReadFromTheLineTheRunEndedWith()
    {
        var cost = AgentRunCostReader.TryRead(RealResultLine);

        Assert.NotNull(cost);
        Assert.Equal(4.713679000000001m, cost!.UsdCost);
        Assert.Equal(65, cost.Turns);
        Assert.Equal(96, cost.InputTokens);
        Assert.Equal(30252, cost.OutputTokens);
        Assert.Equal(5486166, cost.CacheReadTokens);
        Assert.Equal(121263, cost.CacheWriteTokens);
    }

    [Fact]
    public void CachedContextIsNotCountedAsFreshlyPaidInput()
    {
        // "input_tokens" is also the tail of "cache_read_input_tokens". Matching
        // that would report five million cached tokens as bought at full price —
        // exactly inverting the number an operator is looking at this for.
        var cost = AgentRunCostReader.TryRead(RealResultLine);

        Assert.Equal(96, cost!.InputTokens);
        Assert.NotEqual(cost.CacheReadTokens, cost.InputTokens);
    }

    [Fact]
    public void TheDescriptionSaysWhatItCostWithoutMakingAnyoneReadJson()
    {
        var described = AgentRunCostReader.TryRead(RealResultLine)!.Describe();

        Assert.Contains("$4.71", described, StringComparison.Ordinal);
        Assert.Contains("65 turns", described, StringComparison.Ordinal);
        Assert.Contains("5,486,166 cached in", described, StringComparison.Ordinal);
    }

    [Fact]
    public void APartialLineGivesUpWhatItHasRatherThanNothing()
    {
        // The CLI has moved these fields between shapes before. Half an answer
        // still tells an operator which runs were the expensive ones.
        var cost = AgentRunCostReader.TryRead("""{"total_cost_usd":0.42}""");

        Assert.NotNull(cost);
        Assert.Equal(0.42m, cost!.UsdCost);
        Assert.Null(cost.Turns);
        Assert.Equal("$0.42", cost.Describe());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"type":"assistant","message":{"content":[]}}""")]
    [InlineData("The total_cost_usd field is documented in the API reference.")]
    public void ALineThatReportsNoUsageIsNotMistakenForOne(string? line) =>
        Assert.Null(AgentRunCostReader.TryRead(line));
}
