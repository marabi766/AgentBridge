namespace AgentBridge.Abstractions.Interfaces;

/// <summary>
/// An adapter that, when its agent refuses for lack of allowance, remembers when
/// the allowance is expected back and reports <see cref="Models.AgentStatus.RateLimited"/>
/// until then — so the orchestrator waits the reset out rather than ending the run.
/// </summary>
public interface IWaitsOutQuotaLimits
{
    /// <summary>
    /// Discards the remembered reset time, so the agent is treated as available
    /// again immediately.
    ///
    /// This is for the case the bridge cannot see: the operator has signed the
    /// CLI into a different account that still has allowance. The announced reset
    /// is then meaningless — capacity is back now — and continuing to wait for it
    /// would strand the run for hours over nothing.
    /// </summary>
    void ForgetAnnouncedQuotaWait();
}
