using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// Drives Claude Code through <c>claude --print</c> rather than through the
/// Claude desktop window. Everything about running the process lives in
/// <see cref="CommandLineAgentAdapter"/>; only which settings to read is
/// Claude-specific.
/// </summary>
public sealed class ClaudeCliAdapter : CommandLineAgentAdapter
{
    public ClaudeCliAdapter(IConfigurationService configurationService, ILogger<ClaudeCliAdapter> logger)
        : base(configurationService, logger)
    {
    }

    public override string Name => "Claude CLI";

    public override AgentRole Role => AgentRole.Claude;

    protected override CommandLineInvocation ReadInvocation(BridgeConfiguration configuration) => new()
    {
        ConfiguredExecutable = configuration.ClaudeCliExecutable,
        Arguments = configuration.ClaudeCliArguments,
        TimeoutSeconds = configuration.ClaudeCliTimeoutSeconds,
        InstallHint = "Install it with \"npm install -g @anthropic-ai/claude-code\" and sign in, "
            + "or point Claude CLI executable at its full path.",
    };

    /// <summary>
    /// <c>--continue</c> picks up the most recent conversation in the working
    /// directory, which is the project — so the session it resumes is the one
    /// that was working on this repository, not whatever ran last elsewhere.
    /// </summary>
    public override string ResumeArguments(string? arguments) =>
        Contains(arguments, "--continue") || Contains(arguments, "-c")
            ? arguments!
            : $"--continue {arguments}".TrimEnd();

    private static bool Contains(string? arguments, string flag) =>
        SplitArguments(arguments).Contains(flag, StringComparer.Ordinal);
}
