using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RdpHelper.Services;

/// <summary>
/// Windows 自带的 OpenSSH Server（sshd）：安装 / 开关 / 改端口。
/// 安装优先走系统「可选功能」(Add-WindowsCapability)，失败时回退到微软官方 Win32-OpenSSH 的 MSI。
/// 端口写在 %ProgramData%\ssh\sshd_config 的 Port 行。
/// </summary>
public static partial class SshService
{
    public const int DefaultPort = 22;

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "sshd_config");

    public static bool IsInstalled
    {
        get
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\sshd");
            return k != null;
        }
    }

    /// <summary>Running / Stopped / ...；未安装返回空。</summary>
    public static async Task<(string status, string startType)> GetStatusAsync()
    {
        if (!IsInstalled) return ("", "");
        var r = await Shell.PowerShellAsync("$s = Get-Service sshd -ErrorAction SilentlyContinue; if ($s) { \"$($s.Status)|$($s.StartType)\" }");
        var parts = r.Output.Trim().Split('\n').LastOrDefault()?.Trim().Split('|') ?? Array.Empty<string>();
        return parts.Length == 2 ? (parts[0], parts[1]) : ("", "");
    }

    public static async Task<bool> IsRunningAsync() => (await GetStatusAsync()).status == "Running";

    public static async Task InstallAsync(IProgress<string>? progress = null)
    {
        if (IsInstalled) return;
        progress?.Report("正在通过 Windows 可选功能安装 OpenSSH 服务器（可能需要几分钟）…");
        var r = await Shell.PowerShellAsync(@"
try {
  $cap = Get-WindowsCapability -Online -ErrorAction Stop | Where-Object Name -like 'OpenSSH.Server*' | Select-Object -First 1
  if (-not $cap) { throw '系统里找不到 OpenSSH.Server 可选功能' }
  if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name -ErrorAction Stop | Out-Null }
  'OK'
} catch { 'FAIL:' + $_.Exception.Message }", 20 * 60_000);
        if (IsInstalled) return;

        progress?.Report("可选功能安装失败，改为下载微软官方 Win32-OpenSSH 安装包…" + Environment.NewLine + r.Output);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RdpHelper/" + AppPaths.Version);
        var json = await http.GetStringAsync("https://api.github.com/repos/PowerShell/Win32-OpenSSH/releases/latest");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        string? url = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault(u => u != null && u.EndsWith("Win64.msi", StringComparison.OrdinalIgnoreCase)
                                 || u != null && u.Contains("Win64") && u.EndsWith(".msi"));
        if (url == null) throw new InvalidOperationException("没有找到 Win32-OpenSSH 的 MSI 安装包");
        string msi = Path.Combine(Path.GetTempPath(), "OpenSSH-Win64.msi");
        await File.WriteAllBytesAsync(msi, await http.GetByteArrayAsync(url));
        progress?.Report("正在安装 " + Path.GetFileName(url) + " …");
        var m = await Shell.RunAsync("msiexec.exe", $"/i \"{msi}\" /qn /norestart", 10 * 60_000);
        if (!IsInstalled) throw new InvalidOperationException("OpenSSH 安装失败：" + m.Output);
    }

    public static async Task<string> EnableAsync(bool on)
    {
        if (!IsInstalled) throw new InvalidOperationException("请先安装 OpenSSH 服务器");
        var r = await Shell.PowerShellAsync(on
            ? @"try { Set-Service sshd -StartupType Automatic -ErrorAction Stop; Start-Service sshd -ErrorAction Stop } catch { Write-Output ('WARN:启动 sshd 失败：' + $_.Exception.Message) }"
            : @"try { Stop-Service sshd -Force -ErrorAction Stop; Set-Service sshd -StartupType Manual } catch { Write-Output ('WARN:停止 sshd 失败：' + $_.Exception.Message) }");
        var warn = RdpService.CollectWarnings(r);
        var fw = await FirewallService.ApplyAsync(RemoteKind.Ssh, GetPort(), on);
        return string.Join("\n", new[] { warn, fw }.Where(s => s.Length > 0));
    }

    // ---------------- 端口 ----------------

    [GeneratedRegex(@"^\s*Port\s+(\d+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PortLine();

    public static int GetPort()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var m = PortLine().Match(File.ReadAllText(ConfigPath));
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p)) return p;
            }
        }
        catch { /* 无权限等 */ }
        return DefaultPort;
    }

    /// <summary>把 sshd_config 里的 Port 改为 port（只保留一行），然后重启 sshd。</summary>
    public static async Task<string> SetPortAsync(int port)
    {
        if (port != DefaultPort && (port < 1024 || port > 65535))
            throw new ArgumentOutOfRangeException(nameof(port), "端口需在 1024–65535 之间（或默认 22）。");
        if (!IsInstalled) throw new InvalidOperationException("请先安装 OpenSSH 服务器");
        if (port != GetPort() && RdpService.IsListening(port))
            throw new InvalidOperationException($"端口 {port} 已被其他程序占用，请换一个。");

        if (!File.Exists(ConfigPath))
        {
            // sshd 首次启动才会生成配置文件；先启动一次
            await Shell.PowerShellAsync("Start-Service sshd -ErrorAction SilentlyContinue; Start-Sleep 2");
        }
        string text = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";
        File.WriteAllText(ConfigPath, RewritePort(text, port), new UTF8Encoding(false));

        bool running = await IsRunningAsync();
        string warn = "";
        if (running)
        {
            var r = await Shell.PowerShellAsync("try { Restart-Service sshd -Force -ErrorAction Stop } catch { Write-Output ('WARN:重启 sshd 失败：' + $_.Exception.Message) }");
            warn = RdpService.CollectWarnings(r);
        }
        var fw = await FirewallService.ApplyAsync(RemoteKind.Ssh, port, running);
        return string.Join("\n", new[] { warn, fw }.Where(s => s.Length > 0));
    }

    /// <summary>纯函数，便于测试：删除所有生效的 Port 行，在原 #Port 注释处（或开头）写入新端口。</summary>
    public static string RewritePort(string config, int port)
    {
        var lines = config.Replace("\r\n", "\n").Split('\n').ToList();
        lines.RemoveAll(l => PortLine().IsMatch(l));
        int idx = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*#\s*Port\s+\d+", RegexOptions.IgnoreCase));
        string newLine = $"Port {port}";
        if (idx >= 0) lines.Insert(idx + 1, newLine);
        else lines.Insert(0, newLine);
        return string.Join("\r\n", lines);
    }

    public static string ConnectCommand(string host, int port) =>
        port == DefaultPort ? $"ssh {Environment.UserName}@{host}" : $"ssh {Environment.UserName}@{host} -p {port}";
}
