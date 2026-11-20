using WinForms = System.Windows.Forms;

namespace RdpHelper.Services;

/// <summary>任务栏通知区域（托盘）图标：单击 / 双击打开主界面，右键菜单「打开 / 退出」。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private bool _balloonShown;

    public TrayIcon(Action open, Action exit)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(L.T("打开 RdpHelper"), null, (_, _) => open());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("退出 RdpHelper"), null, (_, _) => exit());

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = L.T("RdpHelper 远程桌面助手"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) open(); };
        _icon.DoubleClick += (_, _) => open();
    }

    /// <summary>窗口被收进托盘时提示一次（每次运行只提示一次，避免打扰）。</summary>
    public void NotifyHidden()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(5000, "RdpHelper",
            L.T("内网穿透仍在运行，RdpHelper 已收进托盘。单击托盘图标可重新打开，右键可退出。"),
            WinForms.ToolTipIcon.Info);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } ico) return ico;
        }
        catch { /* 退回系统默认图标 */ }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
