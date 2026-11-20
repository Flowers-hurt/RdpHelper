using System.Net;
using System.Text;

namespace RdpHelper.Services;

public enum RemoteKind { Rdp, Ssh }

/// <summary>
/// 用 Windows 防火墙实现端口放行 + IP 黑白名单。
/// 本程序接管放行规则：系统自带的「远程桌面」规则组 / OpenSSH-Server-In-TCP 会被禁用，
/// 改由 RdpHelper-{RDP|SSH}-* 规则放行：
///   · 白名单为空  → RemoteAddress = Any
///   · 白名单非空  → RemoteAddress = 白名单（其余地址全部拒绝）
///   · 黑名单      → 额外的 Block 规则（Windows 防火墙中 Block 优先于 Allow）
/// 注意：内网穿透的流量来自 127.0.0.1（本机回环），不受防火墙名单限制。
/// </summary>
public static class FirewallService
{
    public static string Prefix(RemoteKind k) => k == RemoteKind.Rdp ? "RdpHelper-RDP-" : "RdpHelper-SSH-";

    private static string BuiltinDisableScript(RemoteKind k) => k == RemoteKind.Rdp
        ? $"try {{ Disable-NetFirewallRule -Group '{RdpService.FirewallGroup}' -ErrorAction Stop }} catch {{ }}"
        : "Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP','sshd' -ErrorAction SilentlyContinue | Disable-NetFirewallRule -ErrorAction SilentlyContinue";

    public static (List<string> allow, List<string> deny) GetPolicy(RemoteKind k)
    {
        var s = AppSettings.Current;
        return k == RemoteKind.Rdp ? (s.RdpAllow, s.RdpDeny) : (s.SshAllow, s.SshDeny);
    }

    public static void SavePolicy(RemoteKind k, IEnumerable<string> allow, IEnumerable<string> deny)
    {
        var s = AppSettings.Current;
        var a = Normalize(allow);
        var d = Normalize(deny);
        if (k == RemoteKind.Rdp) { s.RdpAllow = a; s.RdpDeny = d; }
        else { s.SshAllow = a; s.SshDeny = d; }
        s.Save();
    }

    /// <summary>生成并执行防火墙脚本；enabled=false 时只删除本程序规则。返回警告。</summary>
    public static async Task<string> ApplyAsync(RemoteKind k, int port, bool enabled)
    {
        var (allow, deny) = GetPolicy(k);
        var r = await Shell.PowerShellAsync(BuildScript(k, port, enabled, allow, deny));
        var warns = Shell.Warnings(r.Output);
        if (!r.Ok && warns.Count == 0) warns.Add(L.F("防火墙脚本返回错误：{0}", r.Output));
        return string.Join("\n", warns);
    }

    public static string BuildScript(RemoteKind k, int port, bool enabled, IList<string> allow, IList<string> deny)
    {
        string prefix = Prefix(k);
        string label = k == RemoteKind.Rdp ? "Remote Desktop" : "OpenSSH";
        var sb = new StringBuilder();
        sb.AppendLine($"Get-NetFirewallRule -Name '{prefix}*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue");
        // 以前卸载时留下的普通放行规则（restore-firewall.ps1）会绕过黑白名单，重新接管时一并删除
        sb.AppendLine($"Get-NetFirewallRule -Name 'RdpHelper-Restored-{(k == RemoteKind.Rdp ? "RDP" : "SSH")}-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue");
        sb.AppendLine(BuiltinDisableScript(k));
        if (!enabled) return sb.ToString();

        string allowExpr = allow.Count == 0 ? "'Any'" : "@(" + string.Join(",", allow.Select(Q)) + ")";
        var protocols = k == RemoteKind.Rdp ? new[] { "TCP", "UDP" } : new[] { "TCP" };
        sb.AppendLine("try {");
        foreach (var proto in protocols)
        {
            sb.AppendLine($"  New-NetFirewallRule -Name '{prefix}{proto}' -DisplayName 'RdpHelper {label} ({proto} {port})' -Direction Inbound -Protocol {proto} -LocalPort {port} -RemoteAddress {allowExpr} -Action Allow -Profile Any -ErrorAction Stop | Out-Null");
            if (deny.Count > 0)
                sb.AppendLine($"  New-NetFirewallRule -Name '{prefix}Block-{proto}' -DisplayName 'RdpHelper {label} Blacklist ({proto} {port})' -Direction Inbound -Protocol {proto} -LocalPort {port} -RemoteAddress @({string.Join(",", deny.Select(Q))}) -Action Block -Profile Any -ErrorAction Stop | Out-Null");
        }
        sb.AppendLine("} catch { Write-Output ('WARN:创建防火墙规则失败：' + $_.Exception.Message) }");
        return sb.ToString();
    }

    public static async Task<int> RuleCountAsync(RemoteKind k)
    {
        var r = await Shell.PowerShellAsync($"@(Get-NetFirewallRule -Name '{Prefix(k)}*' -ErrorAction SilentlyContinue | Where-Object {{ $_.Enabled -eq 'True' }}).Count");
        return int.TryParse(r.Output.Trim().Split('\n').LastOrDefault()?.Trim(), out var n) ? n : -1;
    }

    /// <summary>读取规则上实际生效的 RemoteAddress，用于自检。</summary>
    public static async Task<string> RuleRemoteAddressAsync(string ruleName)
    {
        var r = await Shell.PowerShellAsync($"(Get-NetFirewallRule -Name '{ruleName}' -ErrorAction SilentlyContinue | Get-NetFirewallAddressFilter).RemoteAddress -join ','");
        return r.Output.Trim();
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    // ---------------- 地址校验 ----------------

    private static readonly string[] Keywords = { "LocalSubnet", "DNS", "DHCP", "WINS", "DefaultGateway", "Internet", "Intranet" };

    public static List<string> Normalize(IEnumerable<string> items) =>
        items.Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>支持：单个 IP（v4/v6）、CIDR（192.168.1.0/24）、范围（10.0.0.1-10.0.0.50）、LocalSubnet 等关键字。</summary>
    public static bool IsValidEntry(string s, out string error)
    {
        error = "";
        s = s.Trim();
        if (Keywords.Any(k => k.Equals(s, StringComparison.OrdinalIgnoreCase))) return true;
        if (s.Contains('/'))
        {
            var parts = s.Split('/');
            if (parts.Length == 2 && StrictIp(parts[0], out var ip) && int.TryParse(parts[1], out var bits))
            {
                int max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                if (bits >= 0 && bits <= max) return true;
            }
            error = L.F("“{0}” 不是有效的网段（例：192.168.1.0/24）", s);
            return false;
        }
        if (s.Contains('-') && !s.Contains(':'))
        {
            var parts = s.Split('-');
            if (parts.Length == 2 && StrictIp(parts[0].Trim(), out _) && StrictIp(parts[1].Trim(), out _)) return true;
            error = L.F("“{0}” 不是有效的地址范围（例：10.0.0.1-10.0.0.50）", s);
            return false;
        }
        if (StrictIp(s, out _)) return true;
        error = L.F("“{0}” 不是有效的 IP 地址", s);
        return false;
    }

    /// <summary>IPAddress.TryParse 会把 "1.2.3" 这种不完整写法也当成合法 IPv4，这里要求必须是 4 段。</summary>
    private static bool StrictIp(string s, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (s.Contains(':')) return IPAddress.TryParse(s, out ip!);
        var parts = s.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Length > 3 || !p.All(char.IsAsciiDigit) || int.Parse(p) > 255)) return false;
        return IPAddress.TryParse(s, out ip!);
    }
}
