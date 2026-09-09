using AgentBridge.Infrastructure.Agents;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

public sealed class AgentQuotaSignalTests
{
    // The three lines below are quoted from the run of 2026-09-09 that hit the
    // five-hour limit at 08:10 UTC while editing F:\Rasta. The first version of
    // AgentQuotaSignal matched none of them, so a wait that should have ended at
    // 14:20 became a dead run instead. They are here verbatim, epoch and all,
    // because a paraphrase would not have caught that bug either.
    private const string RealRateLimitEvent =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1788951000,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false}}""";

    private const string RealResultLine =
        """{"is_error":true,"num_turns":65,"subtype":"success","api_error_status":429,"result":"You've hit your session limit · resets 2:20pm (Asia/Tehran)","type":"result","duration_ms":687637}""";

    [Fact]
    public void TheStructuredRefusalIsRecognised()
    {
        // The line exactly as it was printed. Its reset has long since passed, so
        // this asserts only what stays true forever: that the refusal is seen.
        // When it lifts is the next test's business.
        Assert.True(AgentQuotaSignal.TryDetect(RealRateLimitEvent, out _));
    }

    [Fact]
    public void TheStructuredRefusalCarriesItsExactReset()
    {
        // Same shape as the real line, with a reset that is still ahead. Pinning
        // the original epoch here made the test pass until that moment arrived
        // and fail every run afterwards — a clock is not a fixture.
        var resetsAt = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds();
        var line =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":"""
            + resetsAt + ""","rateLimitType":"five_hour"}}""";

        Assert.True(AgentQuotaSignal.TryDetect(line, out var announced));
        Assert.NotNull(announced);
        Assert.Equal(resetsAt, announced!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void TheRunsOwnEndingReportIsRecognised()
    {
        // This line states the refusal in prose and as a 429, but the only time
        // it gives is a local wall clock with no date. Recognising the refusal
        // matters; the caller's own fallback covers the missing reset.
        Assert.True(AgentQuotaSignal.TryDetect(RealResultLine, out _));
    }

    [Fact]
    public void AnAllowanceMerelyBeingConsumedIsNotARefusal()
    {
        // The same event announces ordinary usage. Pausing on it would stop a run
        // that has every right to keep going.
        const string allowed =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1788951000,"rateLimitType":"five_hour"}}""";

        Assert.False(AgentQuotaSignal.TryDetect(allowed, out _));
    }

    [Fact]
    public void ClaudesOlderWordingIsStillRecognisedAlongWithWhenItExpires()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        var line = $"Claude AI usage limit reached|{resetsAt.ToUnixTimeSeconds()}";

        Assert.True(AgentQuotaSignal.TryDetect(line, out var announced));
        Assert.NotNull(announced);
        Assert.Equal(resetsAt.ToUnixTimeSeconds(), announced!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void AMillisecondEpochIsNotReadAsSeconds()
    {
        // Reading one as the other puts the reset fifty thousand years out, which
        // would look exactly like a run that never resumes.
        var resetsAt = DateTimeOffset.UtcNow.AddHours(2);
        var line = $"usage limit reached|{resetsAt.ToUnixTimeMilliseconds()}";

        Assert.True(AgentQuotaSignal.TryDetect(line, out var announced));
        Assert.NotNull(announced);
        Assert.True((announced!.Value - resetsAt).Duration() < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"}}")]
    [InlineData("Error: rate limit exceeded, try again later")]
    [InlineData("openai: insufficient_quota")]
    public void TheRefusalWordingsTheToolsActuallyUseAreRecognised(string line) =>
        Assert.True(AgentQuotaSignal.TryDetect(line, out _));

    [Fact]
    public void ARefusalWithoutAStatedResetLeavesTheWaitToTheCaller()
    {
        Assert.True(AgentQuotaSignal.TryDetect("Error: rate limit exceeded", out var announced));
        Assert.Null(announced);
    }

    [Fact]
    public void AResetAlreadyInThePastIsNotBelieved()
    {
        // A stale or misread number must not be read as "resume immediately",
        // which would spend the next attempt on the same refusal.
        var line = $"usage limit reached|{DateTimeOffset.UtcNow.AddHours(-4).ToUnixTimeSeconds()}";

        Assert.True(AgentQuotaSignal.TryDetect(line, out var announced));
        Assert.Null(announced);
    }

    [Fact]
    public void AStructuredRefusalWhoseResetHasPassedStillCountsAsARefusal()
    {
        // Recognising the refusal and knowing when it lifts are separate answers.
        // Losing the first because the second is stale would resend immediately
        // into the same wall.
        var passed = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var stale =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":"""
            + passed + "}}";

        Assert.True(AgentQuotaSignal.TryDetect(stale, out var announced));
        Assert.Null(announced);
    }

    [Theory]
    // An agent writing *about* limits is the false positive that would pause a
    // run for hours over nothing, so the match has to stay narrow.
    [InlineData("Added a usage limit to the audit projector configuration.")]
    [InlineData("The endpoint returns 429 when the caller exceeds its budget.")]
    [InlineData("TODO: document the per-tenant quota and rate limiting design")]
    [InlineData("expect(response.status).toBe(429);")]
    [InlineData("")]
    [InlineData("   ")]
    public void OrdinaryTalkAboutLimitsIsNotARefusal(string line) =>
        Assert.False(AgentQuotaSignal.TryDetect(line, out _));
}
