using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;

namespace AgentBridge.Infrastructure.Services;

/// <summary>
/// Future-UI-facing settings boundary: validates before ever persisting, so a
/// caller can never silently write a broken configuration over a working one.
/// </summary>
public sealed class SettingsService(IConfigurationService configurationService) : ISettingsService
{
    public Task<BridgeConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
        configurationService.LoadAsync(cancellationToken);

    public Task<SettingsValidationResult> ValidateAsync(BridgeConfiguration configuration, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuration.ProjectPath) && !Directory.Exists(configuration.ProjectPath))
        {
            errors.Add($"Project path does not exist: {configuration.ProjectPath}");
        }

        var claudeNameError = ProjectService.GetProtocolFileNameValidationError(configuration.ClaudeReportFileName, "Claude report");
        var codexNameError = ProjectService.GetProtocolFileNameValidationError(configuration.CodexPromptFileName, "Codex prompt");
        if (claudeNameError is not null) errors.Add(claudeNameError);
        if (codexNameError is not null) errors.Add(codexNameError);

        if (string.Equals(configuration.ClaudeReportFileName, configuration.CodexPromptFileName, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Claude report file name and Codex prompt file name must be different.");
        }

        if (configuration.MaximumIterations < 1)
        {
            errors.Add("Maximum iterations must be at least 1.");
        }

        if (configuration.AgentTimeoutSeconds < 1)
        {
            errors.Add("Agent timeout must be at least 1 second.");
        }

        if (configuration.RetryCount < 0)
        {
            errors.Add("Retry count cannot be negative.");
        }

        if (configuration.RetryInitialDelayMilliseconds < 0 || configuration.RetryMaxDelayMilliseconds < 0)
        {
            errors.Add("Retry delays cannot be negative.");
        }

        if (configuration.RetryMaxDelayMilliseconds < configuration.RetryInitialDelayMilliseconds)
        {
            errors.Add("Retry max delay must be greater than or equal to the initial delay.");
        }

        if (configuration.FileDebounceMilliseconds < 0)
        {
            errors.Add("File debounce interval cannot be negative.");
        }

        if (configuration.FileStabilityCheckIntervalMilliseconds < 1)
        {
            errors.Add("File stability check interval must be at least 1ms.");
        }

        if (configuration.FileStabilityRequiredConsecutiveChecks < 1)
        {
            errors.Add("File stability requires at least 1 consecutive check.");
        }

        if (string.IsNullOrWhiteSpace(configuration.ClaudeInstructionTemplate))
        {
            errors.Add("Claude instruction template cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(configuration.CodexInstructionTemplate))
        {
            errors.Add("Codex instruction template cannot be empty.");
        }

        if (!configuration.DryRun)
        {
            if (string.IsNullOrWhiteSpace(configuration.ClaudeConversationIdentifier))
            {
                errors.Add("Claude conversation identifier is required for Live mode.");
            }

            if (string.IsNullOrWhiteSpace(configuration.CodexConversationIdentifier))
            {
                errors.Add("Codex conversation identifier is required for Live mode.");
            }
        }

        return Task.FromResult(errors.Count == 0 ? SettingsValidationResult.Success() : SettingsValidationResult.Failure([.. errors]));
    }

    public async Task<SettingsValidationResult> UpdateAsync(BridgeConfiguration configuration, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(configuration, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return validation;
        }

        await configurationService.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        return validation;
    }
}
