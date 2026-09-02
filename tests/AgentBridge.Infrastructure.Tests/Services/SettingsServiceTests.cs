using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Persistence;
using AgentBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Infrastructure.Tests.Services;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abtests-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsServiceTests() => Directory.CreateDirectory(_dir);

    private SettingsService CreateService() =>
        new(new JsonConfigurationService(Path.Combine(_dir, "settings.json"), NullLogger<JsonConfigurationService>.Instance));

    [Fact]
    public async Task Validate_NonExistentProjectPath_Fails()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with { ProjectPath = Path.Combine(_dir, "does-not-exist") };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not exist"));
    }

    [Fact]
    public async Task Validate_SameFileNameForBothReports_Fails()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with
        {
            ProjectPath = _dir, ClaudeReportFileName = "same.md", CodexPromptFileName = "same.md",
        };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Validate_NonPositiveMaximumIterations_Fails(int maxIterations)
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with { ProjectPath = _dir, MaximumIterations = maxIterations };

        var result = await service.ValidateAsync(config, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task UpdateAsync_ValidConfiguration_PersistsIt()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with { ProjectPath = _dir, MaximumIterations = 7 };

        var result = await service.UpdateAsync(config, CancellationToken.None);
        Assert.True(result.IsValid);

        var reloaded = await service.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(7, reloaded.MaximumIterations);
    }

    [Fact]
    public async Task UpdateAsync_InvalidConfiguration_NeverPersists()
    {
        var service = CreateService();
        var good = BridgeConfiguration.CreateDefault() with { ProjectPath = _dir, MaximumIterations = 7 };
        await service.UpdateAsync(good, CancellationToken.None);

        var bad = good with { MaximumIterations = -5 };
        var result = await service.UpdateAsync(bad, CancellationToken.None);
        Assert.False(result.IsValid);

        var stillGood = await service.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(7, stillGood.MaximumIterations); // the bad config never overwrote the good one
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
