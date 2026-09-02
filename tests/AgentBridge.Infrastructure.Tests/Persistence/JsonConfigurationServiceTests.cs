using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Infrastructure.Tests.Persistence;

public sealed class JsonConfigurationServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "abtests-config-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public JsonConfigurationServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    private JsonConfigurationService CreateService() => new(_path, NullLogger<JsonConfigurationService>.Instance);

    [Fact]
    public async Task Load_WhenFileMissing_ReturnsDefaults()
    {
        var service = CreateService();
        var config = await service.LoadAsync(CancellationToken.None);
        Assert.Equal(BridgeConfiguration.CreateDefault().MaximumIterations, config.MaximumIterations);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var service = CreateService();
        var config = BridgeConfiguration.CreateDefault() with { ProjectPath = "C:/some/project", MaximumIterations = 12, DryRun = false };

        await service.SaveAsync(config, CancellationToken.None);
        var loaded = await service.LoadAsync(CancellationToken.None);

        Assert.Equal("C:/some/project", loaded.ProjectPath);
        Assert.Equal(12, loaded.MaximumIterations);
        Assert.False(loaded.DryRun);
    }

    [Fact]
    public async Task Load_WithCorruptedFile_FallsBackToDefaults_AndBacksUp()
    {
        await File.WriteAllTextAsync(_path, "not json at all {{{");
        var service = CreateService();

        var config = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(BridgeConfiguration.CreateDefault().MaximumIterations, config.MaximumIterations);
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains("corrupted"));
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
