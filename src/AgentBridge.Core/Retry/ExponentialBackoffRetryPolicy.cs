using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Core.Retry;

/// <summary>
/// Exponential backoff with a configurable cap and a hard maximum retry count —
/// this can never become an uncontrolled retry loop. Every delay goes through the
/// injected <see cref="TimeProvider"/> so tests can run it without real wall-clock waits.
/// </summary>
public sealed class ExponentialBackoffRetryPolicy(TimeProvider timeProvider, ILogger<ExponentialBackoffRetryPolicy> logger)
    : IRetryPolicy
{
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        RetryOptions options,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = options.InitialDelay;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < options.MaxRetries && ex is not OperationCanceledException)
            {
                attempt++;
                logger.LogWarning(ex, "Retry {Attempt}/{MaxRetries} after failure, waiting {DelayMs}ms", attempt, options.MaxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * options.BackoffMultiplier, options.MaxDelay.TotalMilliseconds));
            }
        }
    }

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        RetryOptions options,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync<object?>(async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return null;
        }, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ExecuteUntilTrueAsync(
        Func<CancellationToken, Task<bool>> condition,
        RetryOptions options,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = options.InitialDelay;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await condition(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Transient condition attempt {Attempt} failed.", attempt + 1);
            }

            if (attempt >= options.MaxRetries)
            {
                return false;
            }

            attempt++;
            logger.LogDebug(
                "Condition not satisfied; retry {Attempt}/{MaxRetries} after {DelayMs}ms.",
                attempt, options.MaxRetries, delay.TotalMilliseconds);
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(
                Math.Min(delay.TotalMilliseconds * options.BackoffMultiplier, options.MaxDelay.TotalMilliseconds));
        }
    }
}
