using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RdpHelper.Services;
using RdpHelper.Services.Tunnels;

namespace RdpHelper.Testing;

/// <summary>
/// 端到端自检：在真实 Windows（GitHub Actions windows-latest 虚拟机）上把每个功能真正跑一遍。
/// 用法：RdpHelper.exe --selftest report.json   （退出码 0 = 全部通过）
/// 会真实修改本机的远程桌面 / SSH / 防火墙设置，请只在测试机上运行。
/// </summary>
public static class SelfTest
{
    private sealed record Step(string Name, bool Ok, string Detail, long Ms);

    private static readonly List<Step> Steps = new();

    public static async Task<int> RunAsync(string reportPath)
    {
        var skip = (Environment.GetEnvironmentVariable("RDPHELPER_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

        await Run("系统信息", async () =>
        {
            var ed = RdpService.GetEdition();
            await Task.CompletedTask;
            return $"{ed.ProductName} {ed.DisplayVersion} build {ed.Build} EditionID={ed.EditionId} IsHome={ed.IsHome}";
        });

        await Run("局域网 IP", async () =>
        {
            var l = await Task.Run(NetworkService.GetLanAddresses);
            Assert(l.Count > 0, "没有检测到 IPv4 地址");
            return string.Join("; ", l.Select(a => $"{a.Ip} [{a.Adapter}] gw={a.HasGateway} virt={a.IsVirtual}"));
        });

        // ---------------- 远程桌面 ----------------
        await Run("开启远程桌面", async () =>
        {
            if (RdpService.GetPort() != RdpService.DefaultPort) await RdpService.SetPortAsync(RdpService.DefaultPort);
            var w = await RdpService.EnableAsync(true);
            Assert(RdpService.IsEnabled(), "fDenyTSConnections 不是 0");
            Assert(await RdpService.WaitListeningAsync(3389, true, 30_000), "3389 未监听");
            Assert(await FirewallService.RuleCountAsync(RemoteKind.Rdp) == 2, "RdpHelper-RDP 防火墙规则数量不是 2");
            Assert(!await RdpService.IsFirewallGroupEnabledAsync(), "系统自带远程桌面规则应被禁用（由本程序接管）");
            return "OK " + w;
        });

        await Run("RDP 协议握手 127.0.0.1:3389", () => RdpHandshakeAsync("127.0.0.1", 3389));

        await Run("NLA 开关往返", async () =>
        {
            bool orig = RdpService.IsNlaEnabled();
            RdpService.SetNla(!orig);
            Assert(RdpService.IsNlaEnabled() == !orig, "切换失败");
            RdpService.SetNla(orig);
            Assert(RdpService.IsNlaEnabled() == orig, "恢复失败");
            await Task.CompletedTask;
            return $"原值 {orig} → {!orig} → {orig}";
        });

        await Run("修改端口为 13389", async () =>
        {
            var w = await RdpService.SetPortAsync(13389);
            Assert(RdpService.GetPort() == 13389, "注册表 PortNumber 未更新");
            Assert(await RdpService.WaitListeningAsync(13389, true, 30_000), "13389 未监听");
            Assert(await RdpService.WaitListeningAsync(3389, false, 10_000), "3389 仍在监听");
            var ra = await FirewallService.RuleRemoteAddressAsync("RdpHelper-RDP-TCP");
            return $"OK 防火墙 RemoteAddress={ra} {w}";
        });

        await Run("RDP 协议握手 127.0.0.1:13389", () => RdpHandshakeAsync("127.0.0.1", 13389));

        await Run("黑白名单写入防火墙", async () =>
        {
            FirewallService.SavePolicy(RemoteKind.Rdp, new[] { "10.9.8.7", "192.168.50.0/24" }, new[] { "203.0.113.7" });
            await FirewallService.ApplyAsync(RemoteKind.Rdp, RdpService.GetPort(), true);
            var allow = await FirewallService.RuleRemoteAddressAsync("RdpHelper-RDP-TCP");
            var deny = await FirewallService.RuleRemoteAddressAsync("RdpHelper-RDP-Block-TCP");
            Assert(allow.Contains("10.9.8.7") && allow.Contains("192.168.50.0"), "白名单未生效：" + allow);
            Assert(deny.Contains("203.0.113.7"), "黑名单未生效：" + deny);
            Assert(await FirewallService.RuleCountAsync(RemoteKind.Rdp) == 4, "应有 2 条放行 + 2 条拦截规则");
            FirewallService.SavePolicy(RemoteKind.Rdp, Array.Empty<string>(), Array.Empty<string>());
            await FirewallService.ApplyAsync(RemoteKind.Rdp, RdpService.GetPort(), true);
            var after = await FirewallService.RuleRemoteAddressAsync("RdpHelper-RDP-TCP");
            Assert(after.Equals("Any", StringComparison.OrdinalIgnoreCase), "清空后应为 Any：" + after);
            return $"allow={allow} deny={deny} → 清空后 {after}";
        });

        await Run("名单格式校验", async () =>
        {
            string[] good = { "1.2.3.4", "10.0.0.0/8", "10.0.0.1-10.0.0.9", "LocalSubnet", "fe80::1", "2001:db8::/32" };
            string[] bad = { "1.2.3", "10.0.0.0/33", "abc", "1.1.1.1-xyz" };
            foreach (var g in good) Assert(FirewallService.IsValidEntry(g, out _), "应合法：" + g);
            foreach (var b in bad) Assert(!FirewallService.IsValidEntry(b, out _), "应非法：" + b);
            await Task.CompletedTask;
            return "OK";
        });

        await Run("恢复默认端口 3389", async () =>
        {
            await RdpService.SetPortAsync(RdpService.DefaultPort);
            Assert(RdpService.GetPort() == 3389, "注册表未恢复");
            Assert(await RdpService.WaitListeningAsync(3389, true, 30_000), "3389 未恢复监听");
            Assert(await RdpService.WaitListeningAsync(13389, false, 10_000), "13389 仍在监听");
            return "OK";
        });

        // ---------------- SSH ----------------
        if (!skip.Contains("ssh"))
        {
            await Run("sshd_config 端口改写（纯函数）", async () =>
            {
                var cfg = "# comment\r\n#Port 22\r\n#AddressFamily any\r\nPort 2200\r\nSubsystem sftp sftp-server.exe";
                var r = SshService.RewritePort(cfg, 22022);
                Assert(r.Contains("#Port 22\r\nPort 22022"), "未插在 #Port 之后：" + r);
                Assert(!r.Contains("Port 2200\r\n"), "旧 Port 行未删除");
                await Task.CompletedTask;
                return "OK";
            });

            await Run("安装 OpenSSH 服务器", async () =>
            {
                var sw = Stopwatch.StartNew();
                var log = new StringBuilder();
                await SshService.InstallAsync(new Progress<string>(s => log.AppendLine(s)));
                Assert(SshService.IsInstalled, "安装后仍检测不到 sshd 服务");
                return $"用时 {sw.Elapsed.TotalSeconds:0}s {log.ToString().Trim()}";
            });

            await Run("开启 SSH", async () =>
            {
                if (SshService.GetPort() != 22) await SshService.SetPortAsync(22);
                var w = await SshService.EnableAsync(true);
                Assert(await SshService.IsRunningAsync(), "sshd 未运行");
                Assert(await RdpService.WaitListeningAsync(22, true, 20_000), "22 未监听");
                return "OK " + w;
            });

            await Run("SSH 握手 127.0.0.1:22", () => SshBannerAsync("127.0.0.1", 22));

            await Run("SSH 端口改为 22022", async () =>
            {
                var w = await SshService.SetPortAsync(22022);
                Assert(SshService.GetPort() == 22022, "配置文件未更新");
                Assert(await RdpService.WaitListeningAsync(22022, true, 20_000), "22022 未监听");
                Assert(await FirewallService.RuleCountAsync(RemoteKind.Ssh) == 1, "SSH 防火墙规则数量不是 1");
                return "OK " + w;
            });

            await Run("SSH 握手 127.0.0.1:22022", () => SshBannerAsync("127.0.0.1", 22022));

            await Run("SSH 恢复默认 22", async () =>
            {
                await SshService.SetPortAsync(22);
                Assert(await RdpService.WaitListeningAsync(22, true, 20_000), "22 未监听");
                return "OK";
            });
        }

        // ---------------- 内网穿透 ----------------
        if (!skip.Contains("tunnel"))
        {
            await Run("下载 cloudflared", () => InstallAndVersion(TunnelManager.Cloudflare));
            await Run("下载 ngrok", () => InstallAndVersion(TunnelManager.Ngrok));
            await Run("下载 bore", () => InstallAndVersion(TunnelManager.Bore));

            await Run("bore 穿透 RDP 端到端", async () =>
            {
                var addr = await TunnelManager.Bore.StartAsync(3389, RemoteKind.Rdp, TimeSpan.FromSeconds(45));
                try
                {
                    var i = addr.LastIndexOf(':');
                    var r = await Retry(() => RdpHandshakeAsync(addr[..i], int.Parse(addr[(i + 1)..])), 4);
                    return $"{addr} → {r}";
                }
                finally { TunnelManager.Bore.Stop(); }
            });

            await Run("Cloudflare 穿透 RDP 端到端", async () =>
            {
                var cf = TunnelManager.Cloudflare;
                var host = await cf.StartAsync(3389, RemoteKind.Rdp, TimeSpan.FromSeconds(90));
                try
                {
                    await Task.Delay(8000); // 等 trycloudflare 域名生效
                    var r = await Retry(async () =>
                    {
                        int lp = await cf.StartAccessAsync(host);
                        try { return await RdpHandshakeAsync("127.0.0.1", lp); }
                        finally { cf.StopAccess(); }
                    }, 6, 6000);
                    return $"{host} → {r}";
                }
                finally { cf.Stop(); }
            });

            await Run("穿透进程已全部结束", async () =>
            {
                await Task.Delay(1000);
                var left = Process.GetProcessesByName("cloudflared").Length + Process.GetProcessesByName("bore").Length;
                Assert(left == 0, $"还有 {left} 个穿透进程");
                return "OK";
            });
        }

        // ---------------- 复制信息 ----------------
        await Run("生成远程信息 + 剪贴板", async () =>
        {
            var text = RemoteInfo.Build(await SshService.IsRunningAsync());
            Assert(text.Contains(Environment.MachineName), "缺少计算机名");
            Assert(text.Contains("远程桌面"), "缺少远程桌面段落");
            Assert(RemoteInfo.CopyToClipboard(text), "写剪贴板失败");
            Assert(System.Windows.Clipboard.GetText() == text, "剪贴板内容不一致");
            return text;
        });

        await Run("英文界面文案（远程信息 + 译文覆盖）", async () =>
        {
            L.Apply(L.En);
            try
            {
                var text = RemoteInfo.Build(await SshService.IsRunningAsync());
                Assert(text.Contains("Remote Desktop") && text.Contains("Computer name"), "英文远程信息不对：" + text);
                Assert(!System.Text.RegularExpressions.Regex.IsMatch(text, @"[\u4e00-\u9fff]"), "英文远程信息里有中文：" + text);
                foreach (var (zh, en) in L.English)
                    Assert(string.Format(en, 1, 2, 3, 4) is { Length: > 0 }, "译文格式串无效：" + zh);
                Assert(L.Missing.IsEmpty, "缺少译文：" + string.Join(" | ", L.Missing.Keys));
                return $"{L.English.Count} 条译文\n" + text;
            }
            finally { L.Apply(L.Zh); }
        });

        await Run("关闭远程桌面", async () =>
        {
            await RdpService.EnableAsync(false);
            Assert(!RdpService.IsEnabled(), "fDenyTSConnections 不是 1");
            Assert(await FirewallService.RuleCountAsync(RemoteKind.Rdp) == 0, "防火墙规则未清理");
            return "OK";
        });

        // 留在开启状态，方便随后的界面截图展示真实状态
        await Run("重新开启远程桌面（供截图）", async () =>
        {
            await RdpService.EnableAsync(true);
            Assert(await RdpService.WaitListeningAsync(3389, true, 30_000), "3389 未监听");
            return "OK";
        });

        int failed = Steps.Count(s => !s.Ok);
        var report = new
        {
            passed = Steps.Count - failed,
            failed,
            machine = Environment.MachineName,
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            steps = Steps,
        };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return failed == 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task<string>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await action();
            Steps.Add(new Step(name, true, detail, sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            Steps.Add(new Step(name, false, ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds));
        }
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    private static async Task<string> Retry(Func<Task<string>> f, int times, int delayMs = 4000)
    {
        Exception? last = null;
        for (int i = 0; i < times; i++)
        {
            try { return await f(); }
            catch (Exception ex) { last = ex; await Task.Delay(delayMs); }
        }
        throw new Exception($"重试 {times} 次仍失败：{last?.Message}");
    }

    private static async Task<string> InstallAndVersion(TunnelProvider p)
    {
        await p.InstallAsync(null);
        Assert(p.IsInstalled, "下载后文件不存在");
        var v = await p.GetVersionAsync();
        Assert(v.Length > 0, "无法获取版本号");
        return $"{v} ({new FileInfo(p.ExePath).Length / 1024 / 1024} MB)";
    }

    /// <summary>发送 X.224 Connection Request（带 RDP_NEG_REQ），收到 TPKT(0x03 0x00) 开头的 Connection Confirm 即认为是 RDP 服务。</summary>
    public static async Task<string> RdpHandshakeAsync(string host, int port)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(15_000);
        await client.ConnectAsync(host, port, cts.Token);
        var s = client.GetStream();
        byte[] req = { 0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00 };
        await s.WriteAsync(req, cts.Token);
        var buf = new byte[64];
        int n = await s.ReadAsync(buf, cts.Token);
        if (n < 11 || buf[0] != 0x03 || buf[1] != 0x00 || (buf[5] & 0xF0) != 0xD0)
            throw new Exception($"不是 RDP 响应：{BitConverter.ToString(buf, 0, Math.Max(n, 0))}");
        string proto = n >= 19 && buf[11] == 0x02 ? buf[15] switch { 1 => "TLS", 2 => "CredSSP", 8 => "RDSTLS", _ => "0x" + buf[15].ToString("X") } : "standard";
        return $"X.224 Connection Confirm, selectedProtocol={proto}";
    }

    public static async Task<string> SshBannerAsync(string host, int port)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(15_000);
        await client.ConnectAsync(host, port, cts.Token);
        var buf = new byte[256];
        int n = await client.GetStream().ReadAsync(buf, cts.Token);
        var banner = Encoding.ASCII.GetString(buf, 0, n).Trim();
        if (!banner.StartsWith("SSH-2.0")) throw new Exception("不是 SSH banner：" + banner);
        return banner;
    }
}
