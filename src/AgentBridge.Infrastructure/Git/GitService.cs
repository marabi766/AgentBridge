using System.Diagnostics;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Git;

/// <summary>
/// Read-only Git introspection via the `git` CLI (verified present on this machine).
/// Never runs a mutating command (commit/push/pull/reset/checkout) — see class docs
/// on <see cref="IGitService"/>. Arguments are always passed as an argument list,
/// never interpolated into a shell string, so a project path or branch name
/// containing shell metacharacters cannot cause command injection.
/// </summary>
public sealed class GitService(ILogger<GitService> logger) : IGitService
{
    public async Task<GitRepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return GitRepositoryStatus.NotARepository($"Path does not exist: {repositoryPath}");
        }

        var topLevel = await RunGitAsync(repositoryPath, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        if (!topLevel.Success)
        {
            return GitRepositoryStatus.NotARepository(topLevel.StandardError.Trim() is { Length: > 0 } err ? err : "Not a git repository.");
        }

        var branchTask = RunGitAsync(repositoryPath, ["branch", "--show-current"], cancellationToken);
        var statusTask = RunGitAsync(repositoryPath, ["status", "--porcelain=v1"], cancellationToken);
        var logTask = RunGitAsync(repositoryPath, ["log", "-1", "--format=%H%n%an%n%aI%n%s"], cancellationToken);

        await Task.WhenAll(branchTask, statusTask, logTask).ConfigureAwait(false);

        var branchResult = await branchTask.ConfigureAwait(false);
        var statusResult = await statusTask.ConfigureAwait(false);
        var logResult = await logTask.ConfigureAwait(false);

        var modifiedFiles = statusResult.Success
            ? statusResult.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
                .Where(line => line.Length > 0)
                .ToList()
            : new List<string>();

        string? lastCommitHash = null, lastCommitAuthor = null, lastCommitMessage = null;
        DateTimeOffset? lastCommitDate = null;
        if (logResult.Success)
        {
            var lines = logResult.StandardOutput.Split('\n');
            if (lines.Length >= 4)
            {
                lastCommitHash = lines[0].Trim();
                lastCommitAuthor = lines[1].Trim();
                if (DateTimeOffset.TryParse(lines[2].Trim(), out var parsedDate))
                {
                    lastCommitDate = parsedDate;
                }

                lastCommitMessage = string.Join('\n', lines.Skip(3)).Trim();
            }
        }

        return new GitRepositoryStatus
        {
            IsRepository = true,
            RepositoryRoot = topLevel.StandardOutput.Trim(),
            CurrentBranch = branchResult.Success ? EmptyToNull(branchResult.StandardOutput.Trim()) : null,
            IsWorkingTreeClean = modifiedFiles.Count == 0,
            ModifiedFiles = modifiedFiles,
            LastCommitHash = lastCommitHash,
            LastCommitAuthor = lastCommitAuthor,
            LastCommitMessage = lastCommitMessage,
            LastCommitDateUtc = lastCommitDate?.ToUniversalTime(),
        };
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task<GitCommandResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new GitCommandResult(process.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to execute 'git {Args}' in {Dir}.", string.Join(' ', arguments), workingDirectory);
            return new GitCommandResult(false, string.Empty, ex.Message);
        }
    }

    private readonly record struct GitCommandResult(bool Success, string StandardOutput, string StandardError);
}
