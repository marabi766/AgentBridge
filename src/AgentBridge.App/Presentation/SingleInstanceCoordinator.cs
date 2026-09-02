namespace AgentBridge.App;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\AgentBridge.Desktop.Singleton";
    private const string ActivationEventName = "Local\\AgentBridge.Desktop.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task? _listener;

    public SingleInstanceCoordinator(Action activateExisting)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimary = createdNew;
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        if (IsPrimary)
        {
            _listener = Task.Run(() => Listen(activateExisting));
        }
        else
        {
            _activationEvent.Set();
        }
    }

    public bool IsPrimary { get; }

    private void Listen(Action activateExisting)
    {
        var handles = new WaitHandle[] { _activationEvent, _shutdown.Token.WaitHandle };
        while (WaitHandle.WaitAny(handles) == 0) activateExisting();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener?.Wait(TimeSpan.FromSeconds(1));
        _activationEvent.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _shutdown.Dispose();
    }
}
