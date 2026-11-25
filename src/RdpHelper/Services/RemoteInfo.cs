using System.Text;
using System.Windows;
using RdpHelper.Services.Tunnels;

namespace RdpHelper.Services;

/// <summary>生成「一键复制」给别人的远程连接信息（不包含密码）。</summary>
public static class RemoteInfo
{
    /// <summary>sshRunning 由调用方异步查询后传入（查询 sshd 状态要走 PowerShell）。</summary>
    public static string Build(bool sshRunning)
    {
        int port = RdpService.GetPort();
        bool rdpOn = RdpService.IsEnabled();
        int sshPort = SshService.GetPort();
        var lans = NetworkService.GetLanAddresses();
        var real = lans.Where(l => !l.IsVirtual).ToList();
        if (real.Count == 0) real = lans;
        string Addr(string ip) => port == RdpService.DefaultPort ? ip : $"{ip}:{port}";

        var sb = new StringBuilder();
        sb.AppendLine(L.T("【远程连接信息】"));
        sb.AppendLine(L.F("计算机名：{0}", Environment.MachineName));
        sb.AppendLine(L.F("用户名：{0}", UserName()));

        if (rdpOn)
        {
            sb.AppendLine();
            sb.AppendLine(L.F("■ 远程桌面（端口 {0}）", port));
            if (real.Count > 0)
            {
                sb.AppendLine(L.F("局域网地址：{0}", Addr(real[0].Ip)));
                foreach (var l in real.Skip(1).Take(3))
                    sb.AppendLine(L.F("　　备用：{0}（{1}）", Addr(l.Ip), l.Adapter));
            }
            sb.AppendLine(L.T("连接方法：Win+R 输入 mstsc → 计算机填上面的地址 → 用户名/密码填这台电脑的 Windows 账户（微软账户填邮箱和微软账户密码）。"));
            sb.AppendLine(L.T("Mac / 手机：安装微软官方「Windows App」（原 Microsoft Remote Desktop）添加电脑即可。"));
        }

        if (sshRunning)
        {
            sb.AppendLine();
            sb.AppendLine(L.F("■ SSH（端口 {0}）", sshPort));
            if (real.Count > 0) sb.AppendLine(L.F("局域网：{0}", SshService.ConnectCommand(real[0].Ip, sshPort)));
        }

        foreach (var t in TunnelManager.All.Where(t => t.IsRunning && t.PublicAddress != null))
        {
            sb.AppendLine();
            sb.AppendLine(L.F("■ 公网地址（{0} → {1}）：{2}", t.Name, t.Target == RemoteKind.Ssh ? "SSH" : L.T("远程桌面"), t.PublicAddress));
            sb.AppendLine(t.VisitorGuide(t.PublicAddress!, t.Target));
        }

        if (!rdpOn && !sshRunning) sb.AppendLine().AppendLine(L.T("（远程桌面和 SSH 目前都未开启）"));
        sb.AppendLine();
        sb.Append(L.T("※ 密码请单独私下告知，不要和以上信息一起发送。"));
        return sb.ToString();
    }

    public static string UserName()
    {
        var domain = Environment.UserDomainName;
        var user = Environment.UserName;
        return string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? user : $"{domain}\\{user}";
    }

    /// <summary>
    /// 写剪贴板。剪贴板管理器、远程桌面的 rdpclip、输入法等经常短暂占着剪贴板：
    /// 以前用 WPF Clipboard.SetDataObject(copy: true) 每次内部重试 1 秒、外面再重试 6 次，全程阻塞界面线程，
    /// 表现为「未响应」，而且最后还常因 OleFlushClipboard 失败报「被占用」（其实内容已经写进去了）。
    /// 现在直接用 Win32 剪贴板 API：每次尝试只需几毫秒，失败就 await 一小会儿再试（不卡界面），最多约 3 秒。
    /// </summary>
    public static async Task<bool> CopyToClipboardAsync(string text)
    {
        for (int i = 0; i < 40; i++)
        {
            if (Win32Clipboard.TrySetText(text)) return true;
            await Task.Delay(i < 10 ? 30 : 100);
        }
        // 最后兜底用 WPF（不 flush，避免 OleFlushClipboard 再报占用）
        try { Clipboard.SetDataObject(text, false); return true; }
        catch { return false; }
    }
}

internal static class Win32Clipboard
{
    private const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;
    private static System.Windows.Interop.HwndSource? _owner;

    public static bool TrySetText(string text)
    {
        // 必须有一个窗口作为剪贴板所有者（OpenClipboard(NULL) 后 SetClipboardData 可能失败），用一个隐藏的消息窗口
        _owner ??= new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("RdpHelperClipboard")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        if (!OpenClipboard(_owner.Handle)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            int bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return false;
            var ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero) { GlobalFree(h); return false; }
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                System.Runtime.InteropServices.Marshal.WriteInt16(ptr, text.Length * 2, 0);
            }
            finally { GlobalUnlock(h); }
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true; // 成功后内存归系统所有，不能再释放
        }
        finally { CloseClipboard(); }
    }

    /// <summary>自检用：在另一个线程里占住剪贴板 ms 毫秒，模拟剪贴板管理器 / rdpclip 正在读写。</summary>
    internal static Task HoldForTestAsync(int ms) => Task.Run(() =>
    {
        if (!OpenClipboard(IntPtr.Zero)) return;
        try { Thread.Sleep(ms); } finally { CloseClipboard(); }
    });

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);
}
