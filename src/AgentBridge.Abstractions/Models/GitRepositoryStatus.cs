namespace AgentBridge.Abstractions.Models;

public sealed record GitRepositoryStatus
{
    public required bool IsRepository { get; init; }

    public string? RepositoryRoot { get; init; }

    public string? CurrentBranch { get; init; }

    public bool IsWorkingTreeClean { get; init; }

    public IReadOnlyList<string> ModifiedFiles { get; init; } = Array.Empty<string>();

    public string? LastCommitHash { get; init; }

    public string? LastCommitMessage { get; init; }

    public string? LastCommitAuthor { get; init; }

    public DateTimeOffset? LastCommitDateUtc { get; init; }

    public string? Error { get; init; }

    public static GitRepositoryStatus NotARepository(string? error = null) => new()
    {
        IsRepository = false,
        Error = error,
    };
}
