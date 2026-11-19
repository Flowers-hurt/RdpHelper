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
        sb.AppendLine("【远程连接信息】");
        sb.AppendLine($"计算机名：{Environment.MachineName}");
        sb.AppendLine($"用户名：{UserName()}");

        if (rdpOn)
        {
            sb.AppendLine();
            sb.AppendLine($"■ 远程桌面（端口 {port}）");
            if (real.Count > 0)
            {
                sb.AppendLine($"局域网地址：{Addr(real[0].Ip)}");
                foreach (var l in real.Skip(1).Take(3))
                    sb.AppendLine($"　　备用：{Addr(l.Ip)}（{l.Adapter}）");
            }
            sb.AppendLine("连接方法：Win+R 输入 mstsc → 计算机填上面的地址 → 用户名/密码填这台电脑的 Windows 账户（微软账户填邮箱和微软账户密码）。");
            sb.AppendLine("Mac / 手机：安装微软官方「Windows App」（原 Microsoft Remote Desktop）添加电脑即可。");
        }

        if (sshRunning)
        {
            sb.AppendLine();
            sb.AppendLine($"■ SSH（端口 {sshPort}）");
            if (real.Count > 0) sb.AppendLine("局域网：" + SshService.ConnectCommand(real[0].Ip, sshPort));
        }

        foreach (var t in TunnelManager.All.Where(t => t.IsRunning && t.PublicAddress != null))
        {
            sb.AppendLine();
            sb.AppendLine($"■ 公网地址（{t.Name} → {(t.Target == RemoteKind.Ssh ? "SSH" : "远程桌面")}）：{t.PublicAddress}");
            sb.AppendLine(t.VisitorGuide(t.PublicAddress!, t.Target));
        }

        if (!rdpOn && !sshRunning) sb.AppendLine().AppendLine("（远程桌面和 SSH 目前都未开启）");
        sb.AppendLine();
        sb.Append("※ 密码请单独私下告知，不要和以上信息一起发送。");
        return sb.ToString();
    }

    public static string UserName()
    {
        var domain = Environment.UserDomainName;
        var user = Environment.UserName;
        return string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? user : $"{domain}\\{user}";
    }

    /// <summary>剪贴板偶尔被其他程序占用，重试几次。</summary>
    public static bool CopyToClipboard(string text)
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(80);
            }
        }
        return false;
    }
}
