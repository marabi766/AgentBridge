using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using AgentBridge.Infrastructure.Agents;

namespace AgentBridge.Infrastructure.Services;

/// <summary>
/// Resolves the adapter for a role.
///
/// Each agent can be reached two ways — its desktop window or its command line —
/// and which one is in use is an operator setting rather than a deployment
/// decision, so both are registered and the choice is read at the moment an
/// adapter is asked for. Binding it at startup instead would mean the setting
/// only appeared to change until the app was restarted, which is exactly the
/// kind of gap between what the screen says and what is running that this
/// project has spent its time removing.
/// </summary>
public sealed class DefaultAgentAdapterProvider : IAgentAdapterProvider
{
    // The choice is read on demand, but it is read very often — status polling
    // asks for an adapter roughly once a second — so it is held briefly rather
    // than re-reading the settings file every time. Short enough that flipping
    // the setting takes effect while the operator is still looking at it.
    private static readonly TimeSpan DefaultChoiceLifetime = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<IAgentAdapter> _adapters;
    private readonly IConfigurationService _configurationService;
    private readonly TimeSpan _choiceLifetime;
    private readonly object _gate = new();

    private bool _useClaudeCli;
    private bool _useCodexCli;
    private DateTimeOffset _choiceReadAtUtc = DateTimeOffset.MinValue;

    /// <param name="choiceLifetime">
    /// How long a read of the setting stays good for. Only tests pass this: they
    /// change the setting and expect the very next call to reflect it, and
    /// sleeping out the real window instead would put dead seconds in the suite.
    /// </param>
    public DefaultAgentAdapterProvider(
        IEnumerable<IAgentAdapter> adapters,
        IConfigurationService configurationService,
        TimeSpan? choiceLifetime = null)
    {
        _adapters = adapters.ToList();
        _configurationService = configurationService;
        _choiceLifetime = choiceLifetime ?? DefaultChoiceLifetime;
    }

    public IAgentAdapter GetAdapter(AgentRole role)
    {
        var candidates = _adapters.Where(adapter => adapter.Role == role).ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException($"No IAgentAdapter registered for role '{role}'.");
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        // More than one adapter serves this role. Pick by the operator's choice,
        // and fall back to whatever is registered when the preferred one is not,
        // rather than failing the whole run.
        var preferCli = PrefersCommandLine(role);
        return candidates.FirstOrDefault(adapter => IsCommandLine(adapter) == preferCli)
            ?? candidates[0];
    }

    private static bool IsCommandLine(IAgentAdapter adapter) => adapter is CommandLineAgentAdapter;

    private bool PrefersCommandLine(AgentRole role)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _choiceReadAtUtc < _choiceLifetime)
            {
                return Chosen(role);
            }
        }

        BridgeConfiguration configuration;
        try
        {
            configuration = _configurationService.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // An unreadable settings file must not decide which agent runs; keep
            // whatever was last known good.
            lock (_gate)
            {
                return Chosen(role);
            }
        }

        lock (_gate)
        {
            _useClaudeCli = configuration.UseClaudeCli;
            _useCodexCli = configuration.UseCodexCli;
            _choiceReadAtUtc = DateTimeOffset.UtcNow;
            return Chosen(role);
        }
    }

    // Callers hold _gate.
    private bool Chosen(AgentRole role) => role == AgentRole.Claude ? _useClaudeCli : _useCodexCli;
}
