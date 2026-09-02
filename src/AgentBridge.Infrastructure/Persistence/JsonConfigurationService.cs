using System.Text.Json;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Persistence;

public sealed class JsonConfigurationService(string settingsFilePath, ILogger<JsonConfigurationService> logger) : IConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<BridgeConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(settingsFilePath))
            {
                return BridgeConfiguration.CreateDefault();
            }

            try
            {
                var json = await File.ReadAllTextAsync(settingsFilePath, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<BridgeConfiguration>(json, SerializerOptions)
                    ?? BridgeConfiguration.CreateDefault();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogError(ex, "Failed to read settings file {Path}; falling back to defaults.", settingsFilePath);
                try
                {
                    var backupPath = $"{settingsFilePath}.corrupted-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.bak";
                    File.Copy(settingsFilePath, backupPath, overwrite: true);
                    logger.LogWarning("Backed up unreadable settings file to {BackupPath}.", backupPath);
                }
                catch (Exception backupEx)
                {
                    logger.LogWarning(backupEx, "Failed to back up unreadable settings file.");
                }

                return BridgeConfiguration.CreateDefault();
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(BridgeConfiguration configuration, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(configuration, SerializerOptions);
            var tempPath = settingsFilePath + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

            if (File.Exists(settingsFilePath))
            {
                File.Replace(tempPath, settingsFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, settingsFilePath);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
