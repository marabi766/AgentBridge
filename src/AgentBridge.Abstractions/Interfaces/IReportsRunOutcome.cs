namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// Implemented by adapters that can tell the difference between an agent that
/// worked and produced nothing, and one that never got started.
///
/// Both look identical from outside: the instruction went out, the agent stopped,
/// the protocol file is unchanged. But they need opposite handling. The first is
/// a real failure worth stopping for — a permission that could not be granted
/// unattended, a task the agent gave up on. The second is a transient refusal
/// that fixes itself, and stopping an overnight run for it wastes the night.
///
/// Optional on purpose. An adapter that drives a window has no equivalent of an
/// exit code and should not pretend to; the orchestrator treats its absence as
/// "cannot tell" and keeps the cautious behaviour.
/// </summary>
public interface IReportsRunOutcome
{
    /// <summary>
    /// True when the last completed run ended unsuccessfully and so quickly that
    /// it cannot have done the work it was asked to do.
    ///
    /// False while a run is in flight, after a run that did work, and whenever
    /// the reason is already understood by other means — an exhausted allowance
    /// reports itself far more precisely, including when it lifts.
    /// </summary>
    bool LastRunFailedWithoutWorking { get; }
}
