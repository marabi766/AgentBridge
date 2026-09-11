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
    /// Flags <c>codex exec</c> accepts that <c>codex exec resume</c> does not —
    /// confirmed against both commands' own <c>--help</c> on 2026-09-11, not
    /// guessed. The first version of this method assumed <c>resume</c> took
    /// everything <c>exec</c> does; it does not take <c>--sandbox</c> at all
    /// (the policy belongs to the session being resumed, not to resuming it),
    /// and a real run carrying the configured <c>--sandbox workspace-write</c>
    /// forward failed outright — "unexpected argument '--sandbox'" — twice in a
    /// row, which the bridge correctly read as a real failure rather than a
    /// transient one and stopped the run over.
    ///
    /// True means the flag also consumes the token after it.
    /// </summary>
    private static readonly Dictionary<string, bool> ExecOnlyFlags = new(StringComparer.Ordinal)
    {
        ["--sandbox"] = true,
        ["-s"] = true,
        ["--cd"] = true,
        ["-C"] = true,
        ["--profile"] = true,
        ["-p"] = true,
        ["--add-dir"] = true,
        ["--color"] = true,
        ["--local-provider"] = true,
        ["--oss"] = false,
        ["--approve-for-me"] = false,
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

        var kept = new List<string>(parts.Count + 2);
        kept.AddRange(parts.Take(verb + 1));
        kept.Add("resume");
        kept.Add("--last");

        for (var i = verb + 1; i < parts.Count; i++)
        {
            if (ExecOnlyFlags.TryGetValue(parts[i], out var takesValue))
            {
                if (takesValue)
                {
                    i++;
                }

                continue;
            }

            kept.Add(parts[i]);
        }

        return string.Join(' ', kept);
    }
}
