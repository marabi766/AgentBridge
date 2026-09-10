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
            Icon = LoadTrayIcon(),
            Text = "Agent Bridge — starting",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The application's own icon at the size this desktop wants for a tray, so
    /// Windows picks the entry drawn for that size rather than rescaling a larger
    /// one. The tray showed the generic Windows placeholder until now, which is
    /// the icon a user cannot find among a dozen others.
    ///
    /// Falls back rather than throws: no icon in the tray is a poor result, but a
    /// notification service that cannot be constructed takes the whole
    /// application down with it.
    /// </summary>
    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/AgentBridge.ico", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception)
        {
            // Any failure here is cosmetic; the placeholder below still works.
        }

        return Drawing.SystemIcons.Application;
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

    public Task NotifyAsync(
        string title,
        string message,
        NotificationLevel level,
        CancellationToken cancellationToken,
        NotificationAttachment? attachment = null)
    {
        // A tray balloon cannot carry a file; the attachment is for channels that can.
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
