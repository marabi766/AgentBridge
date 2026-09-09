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

    /// <summary>
    /// Codex resumes through a subcommand — <c>exec resume --last</c> — so the
    /// words go immediately after the verb rather than in front of everything.
    /// Putting them first would make "resume" the top-level command and lose
    /// <c>exec</c> along with the non-interactive behaviour that depends on it.
    /// </summary>
    public override string ResumeArguments(string? arguments)
    {
        var parts = SplitArguments(arguments).ToList();
        if (parts.Contains("resume", StringComparer.Ordinal))
        {
            return arguments ?? string.Empty;
        }

        var verb = parts.IndexOf("exec");
        if (verb < 0)
        {
            // No exec to attach to. Say so by leaving the arguments alone: a
            // guessed command line is worse than a run the operator can see did
            // not resume.
            return arguments ?? string.Empty;
        }

        parts.InsertRange(verb + 1, ["resume", "--last"]);
        return string.Join(' ', parts);
    }
}
