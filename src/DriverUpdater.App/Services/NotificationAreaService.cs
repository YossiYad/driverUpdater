using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DriverUpdater.App.Services;

public sealed class NotificationAreaService : IDisposable
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private Icon? _icon;
    private Stream? _iconStream;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public void Show(bool showHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCreated();
        _notifyIcon!.Visible = true;
        if (showHint)
        {
            _notifyIcon.ShowBalloonTip(
                3500,
                "DriverUpdater is still running",
                "Use the notification area icon to open DriverUpdater or exit it completely.",
                Forms.ToolTipIcon.Info);
        }
    }

    public void Hide()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
        }
    }

    private void EnsureCreated()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        var iconResource = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/app.ico"));
        if (iconResource is not null)
        {
            _iconStream = iconResource.Stream;
            _icon = new Icon(_iconStream);
        }

        _menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open DriverUpdater");
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        var exitItem = new Forms.ToolStripMenuItem("Exit DriverUpdater");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(openItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "DriverUpdater",
            Icon = _icon ?? SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = false
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _menu?.Dispose();
        _icon?.Dispose();
        _iconStream?.Dispose();
    }
}
