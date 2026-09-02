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

    public bool ActivateSucceeds { get; set; } = true;

    public bool FindConversationSucceeds { get; set; } = true;

    public bool FindInputBoxSucceeds { get; set; } = true;

    public bool SendMessageSucceeds { get; set; } = true;

    /// <summary>Simulated latency before SendMessage completes — set beyond the configured
    /// agent timeout to exercise timeout handling.</summary>
    public TimeSpan SendMessageDelay { get; set; } = TimeSpan.Zero;

    public AgentStatus Status { get; set; } = AgentStatus.Ready;

    public List<string> SentMessages { get; } = [];

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
    }
}
