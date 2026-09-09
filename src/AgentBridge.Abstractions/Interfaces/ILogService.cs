using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

public interface ILogService
{
    Task<IReadOnlyList<DateOnly>> GetAvailableLogDatesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LogEntry>> ReadLogAsync(DateOnly date, CancellationToken cancellationToken);

    Task<IReadOnlyList<LogEntry>> TailAsync(int maxEntries, CancellationToken cancellationToken);

    /// <summary>
    /// Writes every stored log, oldest first, to <paramref name="destinationPath"/>
    /// as one file. The whole of it rather than what the screen is showing: the
    /// reason to export a log is to hand it to someone who needs what scrolled
    /// past. Returns how many entries were written.
    /// </summary>
    Task<int> ExportAsync(string destinationPath, CancellationToken cancellationToken);

    /// <summary>
    /// Reads what has been appended since <paramref name="position"/>.
    ///
    /// Pass a negative position to start from the end of the log: the result is
    /// then the last <paramref name="maxEntries"/> and a position to resume from,
    /// which is how a follower starts without replaying the whole day.
    /// </summary>
    Task<LogTailResult> ReadSinceAsync(long position, int maxEntries, CancellationToken cancellationToken);
}
