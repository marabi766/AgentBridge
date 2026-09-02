using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Git;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Infrastructure.Tests.Services;

public sealed class ProjectServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abtests-project-" + Guid.NewGuid().ToString("N"));

    public ProjectServiceTests() => Directory.CreateDirectory(_dir);

    private static ProjectService CreateService() => new(new GitService(NullLogger<GitService>.Instance));

    [Fact]
    public async Task ValidateProjectPathAsync_EmptyPath_IsInvalid()
    {
        var service = CreateService();
        var result = await service.ValidateProjectPathAsync("", CancellationToken.None);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ValidateProjectPathAsync_NonExistentPath_IsInvalid()
    {
        var service = CreateService();
        var result = await service.ValidateProjectPathAsync(Path.Combine(_dir, "nope"), CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.False(result.PathExists);
    }

    [Fact]
    public async Task ValidateProjectPathAsync_ExistingNonGitPath_IsValidWithWarning()
    {
        var service = CreateService();
        var result = await service.ValidateProjectPathAsync(_dir, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.False(result.IsGitRepository);
        Assert.Contains(result.Warnings, w => w.Contains("Git repository"));
    }

    [Fact]
    public void GetClaudeReportFilePath_And_GetCodexPromptFilePath_CombineProjectPathAndFileName()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with { ProjectPath = _dir };

        Assert.Equal(Path.Combine(_dir, "ClaudeResultReport.md"), service.GetClaudeReportFilePath(config));
        Assert.Equal(Path.Combine(_dir, "CodexPrompt.md"), service.GetCodexPromptFilePath(config));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
