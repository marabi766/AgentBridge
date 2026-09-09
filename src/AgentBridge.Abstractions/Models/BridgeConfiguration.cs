namespace AgentBridge.Abstractions.Models;

/// <summary>
/// Strongly typed, persisted application configuration. Distinct from
/// <see cref="BridgeStateSnapshot"/> (runtime state) — this is user-editable setup.
/// </summary>
public sealed record BridgeConfiguration
{
    // --- Project ---
    public string ProjectPath { get; init; } = string.Empty;

    public string ClaudeReportFileName { get; init; } = "ClaudeResultReport.md";

    public string CodexPromptFileName { get; init; } = "CodexPrompt.md";

    // --- Claude Desktop ---
    public string? ClaudeExecutablePath { get; init; }

    public string ClaudeProcessName { get; init; } = "Claude";

    public string? ClaudeWindowTitleHint { get; init; } = "Claude";

    public string? ClaudeConversationIdentifier { get; init; }

    public bool AutoLaunchClaude { get; init; }

    // --- ChatGPT Desktop / Codex ---
    public string? ChatGptExecutablePath { get; init; }

    public string ChatGptProcessName { get; init; } = "ChatGPT";

    public string? ChatGptWindowTitleHint { get; init; } = "ChatGPT";

    public string? CodexConversationIdentifier { get; init; }

    public bool AutoLaunchChatGpt { get; init; }

    // --- Claude CLI ---
    // Drives Claude Code as a command line process instead of through the Claude
    // desktop window. On by default, because this is the path that actually
    // holds up: a pipe cannot be stale, frozen, half rendered or occupied by a
    // leftover draft, delivery is confirmed by the process rather than inferred
    // from pixels, and it keeps working while the Windows session is locked —
    // which no window-reading path can. The desktop route stays available for
    // anyone who wants to watch the conversation happen in the app.
    public bool UseClaudeCli { get; init; } = true;

    public string ClaudeCliExecutable { get; init; } = "claude";

    /// <summary>
    /// Arguments for one non-interactive run, space separated. <c>--print</c> is
    /// what makes the run non-interactive; with no prompt argument the CLI reads
    /// the instruction from stdin, which is what makes a long multi-line
    /// instruction safe to pass — no quoting or escaping is involved.
    ///
    /// The permission mode is the counterpart of Codex's <c>workspace-write</c>
    /// sandbox: an unattended loop has nobody to answer a permission prompt, so a
    /// run that has to ask simply stalls and produces no report.
    /// <c>acceptEdits</c> is the conservative choice and still asks before
    /// running commands; operators who want the loop to build and test without
    /// supervision set <c>--dangerously-skip-permissions</c> here.
    /// </summary>
    public string ClaudeCliArguments { get; init; } = "--print --permission-mode acceptEdits";

    /// <summary>How long one Claude run may take before it is abandoned.</summary>
    public int ClaudeCliTimeoutSeconds { get; init; } = 3600;

    // --- Codex CLI ---
    // Drives Codex as a command line process instead of through the ChatGPT
    // desktop window. On by default, for the reasons given above.
    public bool UseCodexCli { get; init; } = true;

    public string CodexCliExecutable { get; init; } = "codex";

    /// <summary>
    /// Arguments for one non-interactive run, space separated. The default asks
    /// Codex to read the instruction from stdin ("-"), which is what makes a long
    /// multi-line prompt safe to pass — no quoting or escaping is involved — and
    /// grants the workspace write access it needs to update the protocol file.
    /// </summary>
    public string CodexCliArguments { get; init; } = "exec --sandbox workspace-write --skip-git-repo-check -";

    /// <summary>How long one Codex run may take before it is abandoned.</summary>
    public int CodexCliTimeoutSeconds { get; init; } = 1800;

    /// <summary>
    /// How many lines of one agent run are written to the log before the rest is
    /// kept for diagnostics only. Zero means no limit.
    ///
    /// The cap exists because an agent running a test suite can print tens of
    /// thousands of lines, and a log nobody can open is no better than no log.
    /// The default is high because rendering shrank each line from a couple of
    /// kilobytes of JSON to a sentence: the same number of lines now costs a
    /// fraction of what it used to, and the interesting part of a long run —
    /// what the tests actually said — was landing past the old limit.
    /// </summary>
    public int AgentLogLineLimit { get; init; } = 20_000;

    // --- Message templates ---
    public string ClaudeInstructionTemplate { get; init; } = DefaultTemplates.ClaudeInstruction;

    public string CodexInstructionTemplate { get; init; } = DefaultTemplates.CodexInstruction;

    // --- File watcher tuning ---
    public int FileDebounceMilliseconds { get; init; } = 400;

    public int FileStabilityCheckIntervalMilliseconds { get; init; } = 300;

    public int FileStabilityRequiredConsecutiveChecks { get; init; } = 3;

    public int FileReadRetryCount { get; init; } = 5;

    public int FileReadRetryDelayMilliseconds { get; init; } = 200;

    // --- Timeouts / retry ---
    // One delivery waits for focus, for the editor to report the draft back and
    // for a send receipt. On a disconnected desktop those readbacks lag badly, so
    // the default has to leave room for them rather than cancel mid-delivery.
    public int AgentTimeoutSeconds { get; init; } = 180;

    public int RetryCount { get; init; } = 3;

    public int RetryInitialDelayMilliseconds { get; init; } = 500;

    public int RetryMaxDelayMilliseconds { get; init; } = 8000;

    // --- Iteration control ---
    public int MaximumIterations { get; init; } = 50;

    // --- Behavior ---
    public bool AutoStart { get; init; }

    public bool StartMinimized { get; init; }

    public bool DarkTheme { get; init; }

    public bool NotificationsEnabled { get; init; } = true;

    public bool DryRun { get; init; } = true;

    public string LoggingLevel { get; init; } = "Information";

    public static BridgeConfiguration CreateDefault() => new();
}

public static class DefaultTemplates
{
    public const string CodexInstruction = """
        Claude has completed the current implementation step.

        The file {{reportFile}} has been updated in the project repository at {{projectPath}}.

        Read the latest {{reportFile}}.

        Then inspect the actual current repository state, relevant source files, Git diff, and relevant test results. Do not rely only on the report.

        Review what Claude actually implemented.

        Determine the next concrete engineering step.

        Then replace the contents of {{promptFile}} with ONLY the actionable prompt that Claude should execute for the next step.

        The prompt must be specific, technically actionable, and based on the actual current state of the repository.

        This is iteration {{iteration}} of at most {{maxIterations}}, on branch {{currentBranch}}.

        Do not modify {{reportFile}}.

        Do not ask the user to manually copy anything.

        When you have finished writing the next prompt, ensure {{promptFile}} contains the complete latest prompt.
        """;

    public const string ClaudeInstruction = """
        Codex has produced the next task in {{promptFile}}.

        Read the latest {{promptFile}}.

        Inspect the current project state at {{projectPath}} as necessary.

        Execute the requested implementation carefully.

        Work directly on the project repository.

        Run the relevant tests, validation, build, or verification steps.

        This is iteration {{iteration}} of at most {{maxIterations}}, on branch {{currentBranch}}.

        When this implementation step is complete, replace the contents of {{reportFile}} with a concise but complete report containing:

        - What was changed
        - Files changed
        - Tests or validation performed
        - Test results
        - Problems encountered
        - Remaining work
        - Recommended next step

        Do not modify {{promptFile}}.

        Do not ask the user to manually copy anything.

        When you have finished the implementation, ensure {{reportFile}} contains the complete latest report.
        """;
}
