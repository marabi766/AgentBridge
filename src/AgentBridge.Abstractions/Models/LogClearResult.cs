namespace AgentBridge.Abstractions.Models;

/// <summary>
/// What a request to clear the stored logs actually managed to do.
///
/// The day's log is held open by the writer for as long as the application runs,
/// so it cannot be removed underneath itself. Reporting the attempt as a plain
/// success would be a lie the operator discovers later, when the file they
/// thought was gone is still on disk.
/// </summary>
public sealed record LogClearResult
{
    public required int FilesDeleted { get; init; }

    /// <summary>Names of logs that could not be removed because they are in use.</summary>
    public required IReadOnlyList<string> FilesInUse { get; init; }
}
