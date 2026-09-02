using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Core.Tests.TestDoubles;

public sealed class StubGitService : IGitService
{
    public GitRepositoryStatus Status { get; set; } = new()
    {
        IsRepository = true,
        CurrentBranch = "main",
        IsWorkingTreeClean = true,
    };

    public Task<GitRepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(Status);
}
