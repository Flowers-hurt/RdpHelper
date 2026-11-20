using WinForms = System.Windows.Forms;

namespace RdpHelper.Services;

/// <summary>
/// 任务栏通知区域（托盘）图标：左键单击 / 双击打开主界面，右键弹出 Win11 风格的 <see cref="Views.TrayMenu"/>
/// （不用 WinForms 的老式 ContextMenuStrip）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private bool _balloonShown;

    public TrayIcon(Action open)
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = L.T("RdpHelper 远程桌面助手"),
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) open();
            else if (e.Button == WinForms.MouseButtons.Right) Views.TrayMenu.Toggle();
        };
        _icon.BalloonTipClicked += (_, _) => open();
    }

    /// <summary>窗口被收进托盘时提示一次（每次运行只提示一次，避免打扰）。</summary>
    public void NotifyHidden()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(5000, "RdpHelper",
            L.T("RdpHelper 已收进托盘，远程桌面、SSH 和内网穿透照常工作。单击托盘图标可重新打开，右键可退出；在设置里可改为关闭即退出。"),
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
