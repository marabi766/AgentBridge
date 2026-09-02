using AgentBridge.Abstractions.Models;

namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// The future-UI-facing settings boundary. Wraps <see cref="IConfigurationService"/>
/// with validation so a caller can never silently persist a broken or overwritten
/// configuration (see project rule: never silently overwrite existing config).
/// </summary>
public interface ISettingsService
{
    Task<BridgeConfiguration> GetCurrentAsync(CancellationToken cancellationToken);

    Task<SettingsValidationResult> ValidateAsync(BridgeConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>Validates, and only persists if validation succeeds.</summary>
    Task<SettingsValidationResult> UpdateAsync(BridgeConfiguration configuration, CancellationToken cancellationToken);
}
