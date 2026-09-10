using System.Text.RegularExpressions;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// Reads the two things a remote control session announces: the short id the
/// CLI hands back when it backgrounds the session, and the link that appears in
/// that session's terminal a few seconds later.
///
/// As with <see cref="AgentQuotaSignal"/>, every pattern here is taken from
/// output that was actually observed, and the tests quote the real lines. The
/// link in particular is not printed to the command that starts the session — it
/// is drawn inside the session's own terminal, so it has to be read back out of
/// the log with the ANSI the terminal drew around it stripped off.
/// </summary>
public static partial class RemoteControlSignal
{
    /// <summary>
    /// The short id from a line like
    /// <c>backgrounded · b627a19b (idle — send a prompt to start)</c>.
    /// </summary>
    public static string? ReadSessionId(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = BackgroundedId().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The remote control link from a session's terminal output.
    ///
    /// The output is a full-screen terminal render, so it arrives wrapped in
    /// escape sequences and carriage returns that would otherwise cut the URL in
    /// half or leave colour codes stuck to it.
    /// </summary>
    public static string? ReadRemoteControlUrl(string? terminalOutput)
    {
        if (string.IsNullOrWhiteSpace(terminalOutput))
        {
            return null;
        }

        var plain = AnsiEscape().Replace(terminalOutput, string.Empty).Replace("\r", string.Empty);
        var match = RemoteControlUrl().Match(plain);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"backgrounded\s*\W\s*([0-9a-f]{6,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundedId();

    // CSI sequences and OSC strings, which is what a TUI paints with.
    [GeneratedRegex(@"\x1b\[[0-9;?]*[a-zA-Z]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"https://claude\.ai/code/[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteControlUrl();
}
