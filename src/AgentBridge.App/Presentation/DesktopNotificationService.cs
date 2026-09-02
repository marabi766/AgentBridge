using AgentBridge.Abstractions.Interfaces;
using AgentBridge.Abstractions.Models;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AgentBridge.App;

public sealed class DesktopNotificationService : INotificationService, IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private bool _notificationsEnabled = true;
    private int _disposed;

    public DesktopNotificationService()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Agent Bridge", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Start / Resume", null, (_, _) => StartRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Pause", null, (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Stop…", null, (_, _) => StopRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "Agent Bridge — starting",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? ExitRequested;

    public void SetNotificationsEnabled(bool enabled) => _notificationsEnabled = enabled;

    public void UpdateStatus(BridgeStatusView status)
    {
        var mode = status.DryRun ? "Dry Run" : "LIVE";
        var text = $"Agent Bridge — {status.StatusText}, {status.CurrentIteration}/{status.MaximumIterations}, {mode}";
        _icon.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    public Task NotifyAsync(string title, string message, NotificationLevel level, CancellationToken cancellationToken)
    {
        if (!_notificationsEnabled || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var icon = level switch
            {
                NotificationLevel.Error => Forms.ToolTipIcon.Error,
                NotificationLevel.Warning => Forms.ToolTipIcon.Warning,
                _ => Forms.ToolTipIcon.Info,
            };
            _icon.ShowBalloonTip(5000, title, message, icon);
        });
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
