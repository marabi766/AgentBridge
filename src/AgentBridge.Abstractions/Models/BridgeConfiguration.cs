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
    public int AgentTimeoutSeconds { get; init; } = 30;

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
