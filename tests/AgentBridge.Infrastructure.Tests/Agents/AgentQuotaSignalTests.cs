using AgentBridge.Infrastructure.Agents;
using Xunit;

namespace AgentBridge.Infrastructure.Tests.Agents;

public sealed class AgentQuotaSignalTests
{
    [Fact]
    public void ClaudesRefusalIsRecognisedAlongWithWhenItExpires()
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

    [Theory]
    // An agent writing *about* limits is the false positive that would pause a
    // run for hours over nothing, so the match has to stay narrow.
    [InlineData("Added a usage limit to the audit projector configuration.")]
    [InlineData("The endpoint returns 429 when the caller exceeds its budget.")]
    [InlineData("TODO: document the per-tenant quota and rate limiting design")]
    [InlineData("")]
    [InlineData("   ")]
    public void OrdinaryTalkAboutLimitsIsNotARefusal(string line) =>
        Assert.False(AgentQuotaSignal.TryDetect(line, out _));
}
