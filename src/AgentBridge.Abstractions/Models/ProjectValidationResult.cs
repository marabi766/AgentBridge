namespace AgentBridge.Abstractions.Models;

public sealed record ProjectValidationResult
{
    public required bool IsValid { get; init; }

    public required bool PathExists { get; init; }

    public required bool IsGitRepository { get; init; }

    public required bool ClaudeReportFileExists { get; init; }

    public required bool CodexPromptFileExists { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record SettingsValidationResult
{
    public required bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public static SettingsValidationResult Success() => new() { IsValid = true };

    public static SettingsValidationResult Failure(params string[] errors) => new()
    {
        IsValid = false,
        Errors = errors,
    };
}
