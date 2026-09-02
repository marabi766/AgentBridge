using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

public interface ILogService
{
    Task<IReadOnlyList<DateOnly>> GetAvailableLogDatesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LogEntry>> ReadLogAsync(DateOnly date, CancellationToken cancellationToken);

    Task<IReadOnlyList<LogEntry>> TailAsync(int maxEntries, CancellationToken cancellationToken);
}
