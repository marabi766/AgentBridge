namespace AgentBridge.Abstractions.Models;

/// <summary>
/// A text document to send alongside a notification — in practice, the protocol
/// file an agent just wrote. Carried as content rather than a path so a notifier
/// does no file I/O of its own and cannot race the writer.
/// </summary>
public sealed record NotificationAttachment
{
    public required string FileName { get; init; }

    public required string Content { get; init; }
}
