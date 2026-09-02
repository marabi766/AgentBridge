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

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("..\\outside.md")]
    [InlineData("nested/report.md")]
    [InlineData("nested\\report.md")]
    [InlineData("C:\\outside.md")]
    public async Task ValidateProjectAsync_ProtocolPathInsteadOfSimpleFileName_IsRejected(string unsafeName)
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = _dir,
            ClaudeReportFileName = unsafeName,
        };

        var result = await service.ValidateProjectAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("simple file name"));
        Assert.Throws<ArgumentException>(() => service.GetClaudeReportFilePath(config));
    }

    [Fact]
    public async Task ValidateProjectAsync_UsesConfiguredProtocolFileNames()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "custom-report.md"), "report");
        await File.WriteAllTextAsync(Path.Combine(_dir, "custom-prompt.md"), "prompt");
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = _dir,
            ClaudeReportFileName = "custom-report.md",
            CodexPromptFileName = "custom-prompt.md",
        };

        var result = await service.ValidateProjectAsync(config, CancellationToken.None);

        Assert.True(result.ClaudeReportFileExists);
        Assert.True(result.CodexPromptFileExists);
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
