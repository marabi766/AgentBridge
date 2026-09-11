using System.Globalization;
using System.Windows.Data;

namespace AgentBridge.App;

/// <summary>
/// Shortens a log entry's category — the logging .NET type's full name, e.g.
/// <c>AgentBridge.Infrastructure.Agents.ClaudeCliAdapter</c> — into what an
/// operator scanning the Activity grid actually wants to know: which agent, or
/// the orchestrator. The 160px column was truncating full names to something
/// like "AgentBridge.Infrastructure.Ac", which told nobody anything.
///
/// Display-only: <see cref="Abstractions.Models.LogEntry.Category"/> itself is
/// left as the exact .NET type name everywhere else — the raw log file on disk,
/// and what Export writes — because that is what someone grepping the file
/// later needs, not a label chosen for a 160px column.
/// </summary>
public sealed class CategoryDisplayConverter : IValueConverter
{
    // Both routes to each agent collapse to the same label; which one is live
    // is a setting, not something the operator needs to re-derive from a class
    // name while reading the log.
    private static readonly Dictionary<string, string> KnownTypeNames = new(StringComparer.Ordinal)
    {
        ["ClaudeCliAdapter"] = "Claude",
        ["ClaudeDesktopAdapter"] = "Claude",
        ["CodexCliAdapter"] = "Codex",
        ["ChatGptDesktopAdapter"] = "Codex",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var category = value as string;
        if (string.IsNullOrEmpty(category))
        {
            return string.Empty;
        }

        var simpleName = category[(category.LastIndexOf('.') + 1)..];
        return KnownTypeNames.TryGetValue(simpleName, out var label) ? label : simpleName;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The Activity grid's category column is read-only.");
}
