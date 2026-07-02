using System.Drawing;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace NetRelay.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;

    public event EventHandler? OpenRequested;
    public event EventHandler? CheckUpdatesRequested;
    public event EventHandler? ExportDiagnosticsRequested;
    public event EventHandler? OpenLogsRequested;
    public event EventHandler? OpenUpdateCacheRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _contextMenu = BuildContextMenu();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "NetRelay 原生网络切换助手",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowBalloonTip(int timeout, string title, string text, Forms.ToolTipIcon icon)
    {
        _notifyIcon.ShowBalloonTip(timeout, title, text, icon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }

    private Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(CreateItem("打开主窗口", (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("检查更新", (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("导出诊断包", (_, _) => ExportDiagnosticsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("打开日志目录", (_, _) => OpenLogsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("打开更新缓存", (_, _) => OpenUpdateCacheRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(CreateItem("退出程序", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
        return menu;
    }

    private static Forms.ToolStripMenuItem CreateItem(string text, EventHandler clickHandler)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += clickHandler;
        return item;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resourceUri = new Uri("pack://application:,,,/Assets/NetRelay.ico");
            var streamInfo = WpfApplication.GetResourceStream(resourceUri);
            if (streamInfo != null)
            {
                using var stream = streamInfo.Stream;
                using var icon = new Icon(stream);
                return (Icon)icon.Clone();
            }
        }
        catch
        {
        }

        try
        {
            var mainModuleFile = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModuleFile))
            {
                return Icon.ExtractAssociatedIcon(mainModuleFile) ?? SystemIcons.Application;
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }
}
