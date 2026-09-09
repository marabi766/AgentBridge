using AgentBridge.Abstractions.Models;

namespace AgentBridge.Fakes;

/// <summary>
/// Mutable knobs a test controls to simulate every scenario in section 31 of the
/// project spec (unavailable, timeout, activation/conversation/input-box failure,
/// send failure) without touching real Claude Desktop or ChatGPT Desktop.
/// </summary>
public sealed class FakeAgentAdapterState
{
    public bool IsApplicationRunning { get; set; } = true;

    public bool LaunchSucceeds { get; set; } = true;

    public bool IsReady { get; set; } = true;

    public bool IsProcessing { get; set; }

    /// <summary>
    /// Whether receiving an instruction makes this agent busy, as a real one is.
    /// Off by default so the tests written before it keep their existing timing;
    /// on, it lets a test model the case where an agent works for a while and
    /// then stops without having written its protocol file.
    /// </summary>
    public bool BecomesBusyOnSend { get; set; }

    public bool ActivateSucceeds { get; set; } = true;

    public bool FindConversationSucceeds { get; set; } = true;

    public bool FindInputBoxSucceeds { get; set; } = true;

    public bool SendMessageSucceeds { get; set; } = true;

    /// <summary>Simulated latency before SendMessage completes — set beyond the configured
    /// agent timeout to exercise timeout handling.</summary>
    public TimeSpan SendMessageDelay { get; set; } = TimeSpan.Zero;

    public AgentStatus Status { get; set; } = AgentStatus.Ready;

    /// <summary>
    /// Reported through <see cref="AgentBridge.Abstractions.Interfaces.IReportsRunOutcome"/>:
    /// the last run ended too quickly to have done anything, so the orchestrator
    /// should retry rather than call the iteration empty.
    /// </summary>
    public bool LastRunFailedWithoutWorking { get; set; }

    public List<string> SentMessages { get; } = [];

    /// <summary>
    /// The subset of <see cref="SentMessages"/> delivered into an existing
    /// session rather than a fresh one. Separate because that difference is the
    /// whole point of Continue and is invisible in the message itself.
    /// </summary>
    public List<string> ResumedMessages { get; } = [];

    public int IsApplicationRunningCallCount { get; internal set; }

    public int IsReadyCallCount { get; internal set; }

    public int ActivateCallCount { get; internal set; }

    public int FindConversationCallCount { get; internal set; }

    public int FindInputBoxCallCount { get; internal set; }

    public int SendMessageCallCount { get; internal set; }

    public void Reset()
    {
        IsApplicationRunning = true;
        LaunchSucceeds = true;
        IsReady = true;
        IsProcessing = false;
        BecomesBusyOnSend = false;
        ActivateSucceeds = true;
        FindConversationSucceeds = true;
        FindInputBoxSucceeds = true;
        SendMessageSucceeds = true;
        SendMessageDelay = TimeSpan.Zero;
        Status = AgentStatus.Ready;
        IsApplicationRunningCallCount = 0;
        IsReadyCallCount = 0;
        ActivateCallCount = 0;
        FindConversationCallCount = 0;
        FindInputBoxCallCount = 0;
        SendMessageCallCount = 0;
        SentMessages.Clear();
        ResumedMessages.Clear();
    }
}
