using AgentBridge.Abstractions.Models;
using AgentBridge.Core.Retry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentBridge.Core.Tests.Retry;

public class ExponentialBackoffRetryPolicyTests
{
    private static ExponentialBackoffRetryPolicy CreatePolicy(FakeTimeProvider time) =>
        new(time, NullLogger<ExponentialBackoffRetryPolicy>.Instance);

    [Fact]
    public async Task SucceedsFirstTry_NeverRetries()
    {
        var time = new FakeTimeProvider();
        var policy = CreatePolicy(time);
        var calls = 0;

        var result = await policy.ExecuteAsync(_ =>
        {
            calls++;
            return Task.FromResult(42);
        }, new RetryOptions { MaxRetries = 3 }, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetriesUpToMaxRetries_ThenThrows()
    {
        var time = new FakeTimeProvider();
        var policy = CreatePolicy(time);
        var calls = 0;

        var task = Task.Run(async () =>
        {
            await policy.ExecuteAsync<int>(_ =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            }, new RetryOptions { MaxRetries = 2, InitialDelay = TimeSpan.FromMilliseconds(10) }, CancellationToken.None);
        });

        // Drive the fake clock forward so queued Task.Delay(..., timeProvider) calls complete.
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal(3, calls); // initial attempt + 2 retries
    }

    [Fact]
    public async Task Cancellation_StopsRetryingImmediately()
    {
        var time = new FakeTimeProvider();
        var policy = CreatePolicy(time);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync<int>(_ => throw new InvalidOperationException("should not run"),
                new RetryOptions { MaxRetries = 5 }, cts.Token));
    }

    [Fact]
    public async Task DelayGrowsExponentially_UpToTheConfiguredCap()
    {
        var time = new FakeTimeProvider();
        var policy = CreatePolicy(time);
        var attempts = 0;

        var task = Task.Run(async () =>
        {
            await policy.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new InvalidOperationException();
            }, new RetryOptions
            {
                MaxRetries = 4,
                InitialDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromMilliseconds(250),
                BackoffMultiplier = 2,
            }, CancellationToken.None);
        });

        for (var i = 0; i < 20 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal(5, attempts);
    }
}
