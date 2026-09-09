using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.Agents;

/// <summary>
/// Drives Codex through <c>codex exec</c> rather than through the ChatGPT
/// desktop window. Everything about running the process lives in
/// <see cref="CommandLineAgentAdapter"/>; only which settings to read is
/// Codex-specific.
/// </summary>
public sealed class CodexCliAdapter : CommandLineAgentAdapter
{
    public CodexCliAdapter(IConfigurationService configurationService, ILogger<CodexCliAdapter> logger)
        : base(configurationService, logger)
    {
    }

    public override string Name => "Codex CLI";

    public override AgentRole Role => AgentRole.Codex;

    protected override CommandLineInvocation ReadInvocation(BridgeConfiguration configuration) => new()
    {
        ConfiguredExecutable = configuration.CodexCliExecutable,
        Arguments = configuration.CodexCliArguments,
        TimeoutSeconds = configuration.CodexCliTimeoutSeconds,
        InstallHint = "Install it with \"npm install -g @openai/codex\" and sign in, "
            + "or point Codex CLI executable at its full path.",
    };
}
