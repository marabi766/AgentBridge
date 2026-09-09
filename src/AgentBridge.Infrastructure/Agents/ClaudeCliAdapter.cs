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
}
