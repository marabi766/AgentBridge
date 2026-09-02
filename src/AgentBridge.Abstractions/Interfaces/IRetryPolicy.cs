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
}
