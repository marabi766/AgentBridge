using System.Diagnostics;
using AgentBridge.Infrastructure.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Infrastructure.Tests.Git;

public sealed class GitServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abtests-git-" + Guid.NewGuid().ToString("N"));

    public GitServiceTests() => Directory.CreateDirectory(_dir);

    private static async Task RunGitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
        }
    }

    [Fact]
    public async Task GetStatus_ForNonRepositoryPath_ReportsNotARepository()
    {
        var service = new GitService(NullLogger<GitService>.Instance);
        var status = await service.GetStatusAsync(_dir, CancellationToken.None);

        Assert.False(status.IsRepository);
    }

    [Fact]
    public async Task GetStatus_ForCleanRepository_ReportsBranchAndCleanTree()
    {
        await RunGitAsync(_dir, "init", "-q", "-b", "main");
        await RunGitAsync(_dir, "config", "user.email", "test@example.com");
        await RunGitAsync(_dir, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(_dir, "readme.txt"), "hello");
        await RunGitAsync(_dir, "add", "readme.txt");
        await RunGitAsync(_dir, "commit", "-q", "-m", "initial commit");

        var service = new GitService(NullLogger<GitService>.Instance);
        var status = await service.GetStatusAsync(_dir, CancellationToken.None);

        Assert.True(status.IsRepository);
        Assert.Equal("main", status.CurrentBranch);
        Assert.True(status.IsWorkingTreeClean);
        Assert.Empty(status.ModifiedFiles);
        Assert.NotNull(status.LastCommitHash);
        Assert.Equal("initial commit", status.LastCommitMessage);
    }

    [Fact]
    public async Task GetStatus_WithModifiedFile_ReportsDirtyTree()
    {
        await RunGitAsync(_dir, "init", "-q", "-b", "main");
        await RunGitAsync(_dir, "config", "user.email", "test@example.com");
        await RunGitAsync(_dir, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(_dir, "readme.txt"), "hello");
        await RunGitAsync(_dir, "add", "readme.txt");
        await RunGitAsync(_dir, "commit", "-q", "-m", "initial commit");

        await File.WriteAllTextAsync(Path.Combine(_dir, "ClaudeResultReport.md"), "# report");

        var service = new GitService(NullLogger<GitService>.Instance);
        var status = await service.GetStatusAsync(_dir, CancellationToken.None);

        Assert.False(status.IsWorkingTreeClean);
        Assert.Contains("ClaudeResultReport.md", status.ModifiedFiles);
    }

    [Fact]
    public async Task GetStatus_NeverMutatesTheRepository()
    {
        await RunGitAsync(_dir, "init", "-q", "-b", "main");
        await RunGitAsync(_dir, "config", "user.email", "test@example.com");
        await RunGitAsync(_dir, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(_dir, "readme.txt"), "hello");
        await RunGitAsync(_dir, "add", "readme.txt");
        await RunGitAsync(_dir, "commit", "-q", "-m", "initial commit");

        var service = new GitService(NullLogger<GitService>.Instance);
        var before = await service.GetStatusAsync(_dir, CancellationToken.None);
        await service.GetStatusAsync(_dir, CancellationToken.None);
        var after = await service.GetStatusAsync(_dir, CancellationToken.None);

        Assert.Equal(before.LastCommitHash, after.LastCommitHash); // no new commit was created by observing status
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                // Git leaves read-only-ish object files on Windows sometimes; force removal.
                foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
