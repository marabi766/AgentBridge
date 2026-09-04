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
        // Do not use createdNew as the ownership test. A prior secondary process
        // can keep the named mutex object alive after the primary exits even though
        // nobody owns it. In that case createdNew is false, but this process must
        // be allowed to acquire the abandoned/unowned mutex and become primary.
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            IsPrimary = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            IsPrimary = true;
        }
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
