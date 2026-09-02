using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// Read-only Git introspection. The bridge is an observer, never a Git client that
/// mutates the repository — implementations must not commit, push, pull, reset or
/// checkout anything.
/// </summary>
public interface IGitService
{
    Task<GitRepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken);
}
