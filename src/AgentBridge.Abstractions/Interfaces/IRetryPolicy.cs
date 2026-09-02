using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

public interface IRetryPolicy
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        RetryOptions options,
        CancellationToken cancellationToken);

    Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        RetryOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Repeats a transient condition until it returns true or the configured
    /// retry budget is exhausted. False results and non-cancellation exceptions
    /// consume the same bounded retry budget.
    /// </summary>
    Task<bool> ExecuteUntilTrueAsync(
        Func<CancellationToken, Task<bool>> condition,
        RetryOptions options,
        CancellationToken cancellationToken);
}
