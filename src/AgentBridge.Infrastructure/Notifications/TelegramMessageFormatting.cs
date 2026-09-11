using AgentBridge.Abstractions.Models;

namespace AgentBridge.Infrastructure.Notifications;

/// <summary>
/// Shared by every message Telegram sends — a notification, a reply to a
/// command — so the operator can tell which project it is about without
/// opening the bridge. One bot per operator is the expected setup, but one
/// operator can easily be running the bridge against more than one project
/// through it, and a message with no project name is ambiguous the moment
/// that happens.
/// </summary>
public static class TelegramMessageFormatting
{
    /// <summary>
    /// The project's folder name — what someone would actually call the
    /// project, unlike its full path — or a fixed fallback when none is
    /// configured yet (first run, or setup incomplete).
    /// </summary>
    public static string ProjectLabel(BridgeConfiguration configuration)
    {
        var path = configuration.ProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Agent Bridge";
        }

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? "Agent Bridge" : name;
    }

    public static string Prefixed(BridgeConfiguration configuration, string body) =>
        $"[{ProjectLabel(configuration)}] {body}";
}
