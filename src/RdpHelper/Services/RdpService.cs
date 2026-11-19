using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace RdpHelper.Services;

public sealed record EditionInfo(string EditionId, string ProductName, string DisplayVersion, string Build, bool IsHome);

/// <summary>
/// Windows 远程桌面（RDP 主机）控制：开关、NLA、端口、防火墙、TermService。
/// 原理与「设置 → 系统 → 远程桌面」一致，都是改下面两个注册表位置：
///   HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server            fDenyTSConnections (0=允许)
///   HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp  PortNumber / UserAuthentication
/// </summary>
public static class RdpService
{
    public const int DefaultPort = 3389;
    private const string TsKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string RdpTcpKey = TsKey + @"\WinStations\RDP-Tcp";

    /// <summary>系统内置「远程桌面」防火墙规则组的资源 ID，不受系统语言影响（中文系统里显示为“远程桌面”）。</summary>
    public const string FirewallGroup = "@FirewallAPI.dll,-28752";

    private static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static EditionInfo GetEdition()
    {
        using var k = Hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string id = k?.GetValue("EditionID") as string ?? "";
        string name = k?.GetValue("ProductName") as string ?? "Windows";
        string disp = k?.GetValue("DisplayVersion") as string ?? (k?.GetValue("ReleaseId") as string ?? "");
        string build = k?.GetValue("CurrentBuildNumber") as string ?? "";
        // 22000+ 是 Windows 11，但 ProductName 仍写 Windows 10，这里修正显示
        if (int.TryParse(build, out var b) && b >= 22000 && name.StartsWith("Windows 10"))
            name = "Windows 11" + name["Windows 10".Length..];
        bool home = id.StartsWith("Core", StringComparison.OrdinalIgnoreCase); // Core / CoreSingleLanguage / CoreCountrySpecific / CoreN
        // 界面自检用：CI 没有家庭版机器，用环境变量模拟家庭版来渲染升级卡片
        if (Environment.GetEnvironmentVariable("RDPHELPER_FAKE_HOME") == "1")
            return new EditionInfo("Core", "Windows 11 Home", disp, build, true);
        return new EditionInfo(id, name, disp, build, home);
    }

    public static bool IsEnabled()
    {
        using var k = Hklm.OpenSubKey(TsKey);
        return Convert.ToInt32(k?.GetValue("fDenyTSConnections", 1) ?? 1) == 0;
    }

    public static int GetPort()
    {
        using var k = Hklm.OpenSubKey(RdpTcpKey);
        var v = k?.GetValue("PortNumber");
        return v == null ? DefaultPort : Convert.ToInt32(v);
    }

    public static bool IsNlaEnabled()
    {
        using var k = Hklm.OpenSubKey(RdpTcpKey);
        return Convert.ToInt32(k?.GetValue("UserAuthentication", 1) ?? 1) == 1;
    }

    public static void SetNla(bool on)
    {
        using var k = Hklm.CreateSubKey(RdpTcpKey, true);
        k.SetValue("UserAuthentication", on ? 1 : 0, RegistryValueKind.DWord);
    }

    public static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    public static async Task<bool> WaitListeningAsync(int port, bool expected, int timeoutMs = 15_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (IsListening(port) == expected) return true;
            await Task.Delay(500);
        }
        return IsListening(port) == expected;
    }

    /// <summary>当前进程是否运行在远程桌面会话里（改端口会把自己踢下线）。</summary>
    public static bool IsInRemoteSession => GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>开启 / 关闭远程桌面。返回警告信息（可能为空）。</summary>
    public static async Task<string> EnableAsync(bool on)
    {
        using (var k = Hklm.CreateSubKey(TsKey, true))
            k.SetValue("fDenyTSConnections", on ? 0 : 1, RegistryValueKind.DWord);

        var warn = await FirewallService.ApplyAsync(RemoteKind.Rdp, GetPort(), on);
        if (on)
        {
            var r = await Shell.PowerShellAsync(@"
try {
  $s = Get-Service TermService -ErrorAction Stop
  if ($s.StartType -eq 'Disabled') { Set-Service TermService -StartupType Manual }
  if ($s.Status -ne 'Running') { Start-Service TermService -ErrorAction Stop }
} catch { Write-Output ('WARN:启动远程桌面服务失败：' + $_.Exception.Message) }");
            warn = Join(warn, CollectWarnings(r));
        }
        return warn;
    }

    /// <summary>修改监听端口：写注册表 → 调整防火墙 → 重启 TermService（远程桌面开启时）。</summary>
    public static async Task<string> SetPortAsync(int port)
    {
        ValidatePort(port);
        int current = GetPort();
        if (port != current && IsListening(port))
            throw new InvalidOperationException(L.F("端口 {0} 已被其他程序占用，请换一个。", port));

        using (var k = Hklm.CreateSubKey(RdpTcpKey, true))
            k.SetValue("PortNumber", port, RegistryValueKind.DWord);

        bool enabled = IsEnabled();
        var warn = await FirewallService.ApplyAsync(RemoteKind.Rdp, port, enabled);
        if (enabled)
        {
            var r = await Shell.PowerShellAsync(RestartTermServiceScript, 120_000);
            warn = Join(warn, CollectWarnings(r));
            if (!await WaitListeningAsync(port, true, 20_000))
                warn = Join(warn, L.F("远程桌面服务已重启，但暂未检测到端口 {0} 在监听，可稍后刷新或重启电脑。", port));
        }
        return warn;
    }

    private static string Join(string a, string b) => string.Join("\n", new[] { a, b }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static void ValidatePort(int port)
    {
        if (port == DefaultPort) return;
        if (port < 1024 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), L.T("端口需在 1024–65535 之间（或默认 3389）。"));
    }

    /// <summary>随机挑一个未被占用的高位端口。</summary>
    public static int SuggestFreePort()
    {
        var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        var rnd = new Random();
        for (int i = 0; i < 200; i++)
        {
            int p = rnd.Next(20000, 60000);
            if (!used.Contains(p)) return p;
        }
        return 33890;
    }

    public static async Task<bool> IsFirewallGroupEnabledAsync()
    {
        var r = await Shell.PowerShellAsync($"@(Get-NetFirewallRule -Group '{FirewallGroup}' -ErrorAction SilentlyContinue | Where-Object {{ $_.Enabled -eq 'True' }}).Count");
        return int.TryParse(r.Output.Trim().Split('\n').LastOrDefault()?.Trim(), out var n) && n > 0;
    }

    // Restart-Service 偶尔卡在 Stopping，超时后仅在 TermService 独占进程时强杀再拉起
    private const string RestartTermServiceScript = @"
$job = Start-Job { Restart-Service TermService -Force -ErrorAction Stop }
if (-not (Wait-Job $job -Timeout 45)) {
  Stop-Job $job
  $svcPid = (Get-CimInstance Win32_Service -Filter ""Name='TermService'"").ProcessId
  if ($svcPid -and @(Get-CimInstance Win32_Service -Filter ""ProcessId=$svcPid"").Count -le 1) {
    Stop-Process -Id $svcPid -Force -ErrorAction SilentlyContinue
    Start-Sleep 2
  }
}
Receive-Job $job -ErrorAction SilentlyContinue | Out-Null
if ((Get-Service TermService).Status -ne 'Running') {
  try { Start-Service TermService -ErrorAction Stop } catch { Write-Output ('WARN:重启远程桌面服务失败：' + $_.Exception.Message) }
}
";

    internal static string CollectWarnings(Shell.Result r)
    {
        var s = string.Join("\n", Shell.Warnings(r.Output));
        if (!r.Ok && string.IsNullOrEmpty(s)) s = L.F("PowerShell 返回错误：{0}", r.Output);
        return s;
    }
}
