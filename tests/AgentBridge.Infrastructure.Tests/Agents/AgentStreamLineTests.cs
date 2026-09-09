using AgentBridge.Infrastructure.Agents;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

/// <summary>
/// Every line quoted here was taken from a real run against F:\Rasta on
/// 2026-09-09, shortened only where a signature blob ran to kilobytes. Inventing
/// the shapes would have tested a format nobody emits.
/// </summary>
public sealed class AgentStreamLineTests
{
    [Fact]
    public void WhatTheAgentSaidSurvives()
    {
        const string line =
            """{"type":"assistant","message":{"model":"claude-opus-5","role":"assistant","content":[{"type":"text","text":"Now the status-cascade persistence gap:"}]}}""";

        Assert.Equal("Now the status-cascade persistence gap:", AgentStreamLine.Render(line));
    }

    [Fact]
    public void AToolCallKeepsTheDetailThatSaysWhichOneItWas()
    {
        // "Edit" alone could be any file in the repository. The path is the whole
        // reason to log the line.
        const string line =
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01","name":"Edit","input":{"replace_all":false,"file_path":"F:\\Rasta\\services\\audit-service\\src\\audit\\audit.repository.ts"}}]}}""";

        var rendered = AgentStreamLine.Render(line);

        Assert.NotNull(rendered);
        Assert.Contains("[Edit]", rendered!, StringComparison.Ordinal);
        Assert.Contains("audit.repository.ts", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingResultKeepsTheExplanationRatherThanJustTheSubtype()
    {
        // This exact line is the only place the 403 that ended a real run said
        // what was wrong. Reporting "finished: success" from its subtype would
        // have been worse than saying nothing.
        const string line =
            """{"is_error":true,"num_turns":34,"subtype":"success","api_error_status":403,"result":"Failed to authenticate. API Error: 403 Request not allowed","type":"result","duration_ms":618082}""";

        var rendered = AgentStreamLine.Render(line);

        Assert.NotNull(rendered);
        Assert.Contains("error", rendered!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("403 Request not allowed", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulResultSaysSoWithWhatItCost()
    {
        const string line =
            """{"type":"result","subtype":"success","is_error":false,"total_cost_usd":4.713679000000001}""";

        var rendered = AgentStreamLine.Render(line);

        Assert.NotNull(rendered);
        Assert.Contains("finished: success", rendered!, StringComparison.Ordinal);
        Assert.Contains("4.71", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ARetryIsWorthSeeing()
    {
        const string line =
            """{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"retry_delay_ms":1120,"error":"unknown"}""";

        Assert.Equal("API retry 2/10", AgentStreamLine.Render(line));
    }

    [Theory]
    // A thinking signature: kilobytes of base64 with nothing in it for a reader.
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"CAISiQUKpgEIERgCKkBPH2FM"}]}}""")]
    // Token counters tick several times a second.
    [InlineData("""{"type":"system","subtype":"thinking_tokens","estimated_tokens":150,"estimated_tokens_delta":100}""")]
    // init lists every tool and directory at startup.
    [InlineData("""{"type":"system","subtype":"init","cwd":"F:\\Rasta","tools":["Task","Bash","Edit"]}""")]
    // The repository answering the agent, not the agent working.
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01","type":"tool_result","content":"The file has been updated successfully."}]}}""")]
    // Already reported, and better, by the quota signal.
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1788969000}}""")]
    public void NoiseIsDropped(string line) => Assert.Null(AgentStreamLine.Render(line));

    [Fact]
    public void CodexsOrdinaryProseIsLeftExactlyAsItIs()
    {
        // Codex does not emit JSON at all. Anything that is not this shape has to
        // pass through untouched, or half the loop's output disappears.
        const string line = "  * and before the repository is reached. A cached value";

        Assert.Equal(line, AgentStreamLine.Render(line));
    }

    [Fact]
    public void ALineCutShortByALengthCapIsKeptRatherThanSwallowed()
    {
        // A truncated event no longer parses. Dropping it for that would lose the
        // longest lines, which are not the least interesting ones.
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"half a sen""";

        Assert.Equal(line, AgentStreamLine.Render(line));
    }

    [Fact]
    public void AMultiLineMessageIsFlattenedSoOneEventStaysOneLine()
    {
        // The log is read back line by line, by this project's own parser among
        // others. An embedded newline would split one event into two entries, the
        // second of which parses as nothing.
        const string line =
            """{"type":"assistant","message":{"content":[{"type":"text","text":"first line\nsecond line"}]}}""";

        var rendered = AgentStreamLine.Render(line);

        Assert.Equal("first line second line", rendered);
        Assert.DoesNotContain('\n', rendered!);
    }
}
