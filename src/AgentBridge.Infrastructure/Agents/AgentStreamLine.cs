using System.Text;
using System.Text.Json;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// Turns one line of an agent's machine-readable output into the line a person
/// would want to read.
///
/// Asked for stream-json, Claude Code emits one JSON object per event: a two
/// kilobyte object whose interesting part is a single sentence, another whose
/// entire content is an opaque thinking signature, another counting tokens. Put
/// straight into a log they bury what happened under what it was encoded as, and
/// the Activity page becomes a wall of braces nobody reads.
///
/// So the rendering happens here, before anything is written. What survives is
/// what the agent actually said and did. The raw text is still kept in the run
/// transcript, which is what Diagnostics shows, so nothing is lost — it is moved
/// to where it belongs.
/// </summary>
public static class AgentStreamLine
{
    /// <summary>
    /// The readable form of <paramref name="line"/>, or null when it carries
    /// nothing worth showing.
    ///
    /// Anything that is not this particular JSON shape is returned unchanged:
    /// Codex prints ordinary prose, and a line cut short by a length cap is no
    /// longer parseable. Neither should be swallowed for failing to look like an
    /// event.
    /// </summary>
    public static string? Render(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return line;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return line;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return line;
        }

        return ReadString(root, "type") switch
        {
            "assistant" => RenderAssistant(root),
            "result" => RenderResult(root),
            "system" => RenderSystem(root),
            // A tool result is the repository answering the agent, not the agent
            // working. It is long, it is already on disk, and the next assistant
            // line says what was made of it.
            "user" => null,
            // Reported separately, and far better, by the quota signal.
            "rate_limit_event" => null,
            _ => null,
        };
    }

    private static string? RenderAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var rendered = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            switch (ReadString(block, "type"))
            {
                case "text":
                    Append(rendered, Collapse(ReadString(block, "text")));
                    break;

                case "tool_use":
                    // The name alone rarely says enough — "Edit" could be any file
                    // in the repository — and the whole input is far too much.
                    // Whichever of these it carries is the part that identifies it.
                    var name = ReadString(block, "name");
                    var detail = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                        ? ReadString(input, "command")
                          ?? ReadString(input, "file_path")
                          ?? ReadString(input, "pattern")
                          ?? ReadString(input, "path")
                          ?? ReadString(input, "description")
                        : null;
                    Append(rendered, detail is null ? $"[{name}]" : $"[{name}] {Collapse(detail)}");
                    break;

                // "thinking" carries an empty string and a signature blob. There
                // is nothing in it for a reader.
            }
        }

        return rendered.Length == 0 ? null : rendered.ToString();
    }

    private static string RenderResult(JsonElement root)
    {
        var subtype = ReadString(root, "subtype") ?? "ended";
        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        var status = ReadString(root, "result");
        var cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
            ? $" (${c.GetDouble():F2})"
            : string.Empty;

        // On the failing path the "result" field is the whole explanation — the
        // 403 that ended a real run said so only here.
        return isError && !string.IsNullOrWhiteSpace(status)
            ? $"finished with an error: {Collapse(status)}{cost}"
            : $"finished: {subtype}{cost}";
    }

    private static string? RenderSystem(JsonElement root) => ReadString(root, "subtype") switch
    {
        "api_retry" =>
            $"API retry {ReadNumber(root, "attempt")}/{ReadNumber(root, "max_retries")}"
            + (ReadString(root, "error") is { Length: > 0 } why and not "unknown" ? $" after {why}" : string.Empty),
        // init lists every tool and directory at startup; the token counters tick
        // several times a second. Both are noise in a log meant to be read.
        _ => null,
    };

    private static void Append(StringBuilder builder, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(text);
    }

    /// <summary>
    /// Flattens whitespace so one event stays one line. A multi-line message
    /// would otherwise break the log's one-entry-per-line shape, and every reader
    /// of it — including this project's own log parser — reads by line.
    /// </summary>
    private static string? Collapse(string? text) => text is null
        ? null
        : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.ToString()
            : "?";
}
