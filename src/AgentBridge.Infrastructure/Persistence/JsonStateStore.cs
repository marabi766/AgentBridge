using System.Text.Json;
using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Persistence;

/// <summary>
/// Atomic JSON persistence for <see cref="BridgeStateSnapshot"/>: writes go to a
/// temp file in the same directory then <see cref="File.Replace(string,string,string?)"/>
/// / <see cref="File.Move(string,string,bool)"/> into place, so a crash mid-write can
/// never leave a half-written state file. A file that fails to parse is preserved as
/// a timestamped backup and reported as Corrupted rather than silently discarded or
/// silently accepted.
/// </summary>
public sealed class JsonStateStore(string stateFilePath, ILogger<JsonStateStore> logger) : IStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<StateLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(stateFilePath))
            {
                return StateLoadResult.NotFound();
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(stateFilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "Failed to read state file {Path}.", stateFilePath);
                return StateLoadResult.Corrupted($"I/O error reading state file: {ex.Message}", backupFilePath: null);
            }

            try
            {
                var snapshot = JsonSerializer.Deserialize<BridgeStateSnapshot>(json, SerializerOptions);
                if (snapshot is null)
                {
                    var backup = BackupCorruptFile(json);
                    return StateLoadResult.Corrupted("State file deserialized to null.", backup);
                }

                return StateLoadResult.Loaded(snapshot);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "State file {Path} is not valid JSON.", stateFilePath);
                var backup = BackupCorruptFile(json);
                return StateLoadResult.Corrupted($"State file is not valid JSON: {ex.Message}", backup);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(BridgeStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(stateFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            var tempPath = stateFilePath + $".{Guid.NewGuid():N}.tmp";

            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

            if (File.Exists(stateFilePath))
            {
                File.Replace(tempPath, stateFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, stateFilePath);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(stateFilePath))
            {
                File.Delete(stateFilePath);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private string? BackupCorruptFile(string originalContent)
    {
        try
        {
            var backupPath = $"{stateFilePath}.corrupted-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.bak";
            File.WriteAllText(backupPath, originalContent);
            logger.LogWarning("Backed up corrupted state file to {BackupPath}.", backupPath);
            return backupPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to back up corrupted state file {Path}.", stateFilePath);
            return null;
        }
    }
}
