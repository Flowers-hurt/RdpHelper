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

            await Run("用自带安装包安装 OpenSSH（真实执行 MSI）", async () =>
            {
                // runner 预装了系统版 OpenSSH（卸载要重启），这里强制走自带 MSI，装完后 sshd 应指向 Program Files\OpenSSH
                var msi = SshInstaller.BundledMsi;
                Assert(msi != null, "exe 旁边的 redist 目录里没有自带的 OpenSSH MSI");
                var sw = Stopwatch.StartNew();
                await SshInstaller.RunAsync(force: true);
                var r = await Shell.PowerShellAsync("(Get-CimInstance Win32_Service -Filter \"Name='sshd'\").PathName");
                var path = r.Output.Trim();
                Assert(SshService.IsInstalled, "安装后仍检测不到 sshd 服务");
                Assert(path.Contains(@"Program Files\OpenSSH", StringComparison.OrdinalIgnoreCase), "sshd 不是来自自带 MSI：" + path);
                Assert(SshInstaller.Percent == 1 && SshInstaller.LastError == null, "安装状态不对：" + SshInstaller.Stage);
                Assert(!SshInstaller.IsRunning, "安装任务没有结束");
                return $"用时 {sw.Elapsed.TotalSeconds:0}s · {Path.GetFileName(msi)} · {path}\n" + string.Join("\n", SshInstaller.Log);
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

            await Run("sshd_config 密码登录开关改写（纯函数）", async () =>
            {
                await Task.CompletedTask;
                var cfg = "Port 22\r\n#PasswordAuthentication yes\r\nPasswordAuthentication yes\r\n\r\nMatch Group administrators\r\n       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys\r\n       PasswordAuthentication yes\r\n";
                var off = SshKeyService.RewritePasswordAuth(cfg, false);
                Assert(off.Contains("#PasswordAuthentication yes\r\nPasswordAuthentication no\r\n"), "全局段没有写入 no：" + off);
                Assert(off.Contains("Match Group administrators\r\n       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys\r\n       PasswordAuthentication yes"), "Match 段被改动了：" + off);
                Assert(off.Split("PasswordAuthentication no").Length == 2, "出现了多行设置");
                Assert(SshKeyService.RewritePasswordAuth(off, true).Contains("\r\nPasswordAuthentication yes\r\n\r\nMatch"), "改回 yes 失败");
                Assert(SshKeyService.Parse("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIK0wmN/Cr3JXqmLW7u+g9pTh+wyqDHpSQEIQczXkVx9q a@b") != null, "合法公钥被判为无效");
                Assert(SshKeyService.Parse("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIK0wmN/Cr3JXqmLW7u+g9pTh+wyqDHpSQEIQczXkVx9q") == null, "类型与内容不符的公钥应判为无效");
                return "OK";
            });

            await Run("SSH 公钥免密码登录 + 禁止密码登录（真实 ssh 连接）", async () =>
            {
                var me = await AccountService.GetCurrentAsync();
                var dir = Path.Combine(Path.GetTempPath(), "rdph-key-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(dir);
                var key = Path.Combine(dir, "id_ed25519");
                var g = await Shell.RunAsync("ssh-keygen.exe", $"-t ed25519 -N \"\" -C rdphelper-selftest -q -f \"{key}\"", 30_000);
                Assert(File.Exists(key + ".pub"), "ssh-keygen 失败：" + g.Output);
                var pub = File.ReadAllText(key + ".pub").Trim();
                Assert(await SshKeyService.AddAsync(me.IsAdmin, pub) == 1, "添加公钥失败");
                Assert(await SshKeyService.AddAsync(me.IsAdmin, pub) == 0, "重复公钥没有被跳过");
                var fp = SshKeyService.Parse(pub)!.Fingerprint;
                var common = "-o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o ConnectTimeout=15";
                string target = $"{me.User}@127.0.0.1";
                async Task<Shell.Result> Ssh(string extra) => await Shell.RunAsync("ssh.exe", $"{common} {extra} {target} echo RDPH_OK", 60_000);

                var withKey = await Ssh($"-i \"{key}\"");
                Assert(withKey.Output.Contains("RDPH_OK"), $"公钥登录失败（{SshKeyService.KeysFile(me.IsAdmin)}）：" + withKey.Output);
                var noKeyBefore = await Ssh("-o PubkeyAuthentication=no");
                Assert(noKeyBefore.Output.Contains("password", StringComparison.OrdinalIgnoreCase), "默认应允许密码登录：" + noKeyBefore.Output);

                var w = await SshKeyService.SetPasswordAuthAsync(false, me.IsAdmin);
                Assert(!SshKeyService.PasswordAuthEnabled(), "配置文件未写入");
                await RdpService.WaitListeningAsync(22, true, 20_000);
                var noKeyAfter = await Ssh("-o PubkeyAuthentication=no");
                var denied = noKeyAfter.Output.Split('\n').FirstOrDefault(l => l.Contains("Permission denied")) ?? noKeyAfter.Output;
                Assert(!denied.Contains("password"), "关闭后服务器仍提供密码登录：" + denied);
                var withKeyAfter = await Ssh($"-i \"{key}\"");
                Assert(withKeyAfter.Output.Contains("RDPH_OK"), "关闭密码登录后公钥登录失败：" + withKeyAfter.Output);

                await SshKeyService.SetPasswordAuthAsync(true, me.IsAdmin);
                await SshKeyService.RemoveAsync(me.IsAdmin, fp);
                Assert(SshKeyService.List(me.IsAdmin).All(k => k.Fingerprint != fp), "公钥未删除");
                await RdpService.WaitListeningAsync(22, true, 20_000);
                var afterRemove = await Ssh($"-i \"{key}\"");
                Assert(!afterRemove.Output.Contains("RDPH_OK"), "删除公钥后仍能登录");
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
                return $"账户 {me.User} admin={me.IsAdmin} · {fp}\n关闭前：{noKeyBefore.Output.Trim()}\n关闭后：{denied.Trim()} {w}";
            });
        }

        // ---------------- 账户 ----------------
        await Run("当前账户检测", async () =>
        {
            var me = await AccountService.GetCurrentAsync();
            Assert(!(me.Kind == AccountKind.Local && me.PasswordlessOnly), "本地账户不应提示「仅允许 Windows Hello」");
            return $"{me.User} kind={me.Kind} hasPassword={me.HasPassword} admin={me.IsAdmin} passwordlessOnly={me.PasswordlessOnly} RDP 组={AccountService.RdpUsersGroup}";
        });

        await Run("远程专用账户：创建 / 登录 / 改密码 / 禁用 / 删除", async () =>
        {
            const string name = "rdph_selftest";
            if (AccountService.ListRemoteAccounts().Any(a => a.Name == name)) await AccountService.DeleteRemoteAccountAsync(name);
            string p1 = "Rh!" + Guid.NewGuid().ToString("N")[..12] + "a9", p2 = "Rh#" + Guid.NewGuid().ToString("N")[..12] + "b8";
            Assert(AccountService.ValidateUserName("bad/name") != null && AccountService.ValidateUserName(name) == null, "用户名校验不对");
            AccountService.CreateRemoteAccount(name, p1, admin: false);
            try
            {
                var acc = AccountService.ListRemoteAccounts().FirstOrDefault(a => a.Name == name);
                Assert(acc is { Enabled: true, InRdpGroup: true, IsAdmin: false }, "账户状态不对：" + acc);
                Assert(AccountService.TestNetworkLogon(name, p1), "新账户无法用密码网络登录");
                Assert(AccountService.HasPassword(name) == true, "空密码检测误判");
                AccountService.SetPassword(name, p2);
                Assert(AccountService.TestNetworkLogon(name, p2) && !AccountService.TestNetworkLogon(name, p1), "改密码后新旧密码校验不对");
                AccountService.SetEnabled(name, false);
                Assert(!AccountService.TestNetworkLogon(name, p2) && AccountService.ListRemoteAccounts().First(a => a.Name == name).Enabled == false, "禁用无效");
                AccountService.SetEnabled(name, true);
                Assert(AccountService.TestNetworkLogon(name, p2), "重新启用无效");
                try { AccountService.CreateRemoteAccount(name, p1, false); Assert(false, "重复创建应失败"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("已经存在")) { }
            }
            finally
            {
                await AccountService.DeleteRemoteAccountAsync(name);
            }
            Assert(AccountService.ListRemoteAccounts().All(a => a.Name != name), "删除失败");
            return $"OK · 组 {AccountService.RdpUsersGroup}";
        });

        // ---------------- 内网穿透 ----------------
        if (!skip.Contains("tunnel"))
        {
            await Run("下载 cloudflared", () => InstallAndVersion(TunnelManager.Cloudflare));
            await Run("下载 ngrok", () => InstallAndVersion(TunnelManager.Ngrok));
            await Run("内置 bore（不用联网下载即可使用）", async () =>
            {
                var b = TunnelManager.Bore;
                Assert(b.IsBundled, "安装目录 redist 里没有内置的 bore.exe：" + b.BundledPath);
                if (File.Exists(b.ExePath)) File.Delete(b.ExePath);
                Assert(b.IsInstalled, "有内置 bore 时应视为已安装");
                Assert(File.Exists(b.ExePath), "应把内置 bore 拷到组件目录");
                var v = await b.GetVersionAsync();
                Assert(v.Length > 0, "内置 bore 无法运行");
                return $"{v} ({new FileInfo(b.BundledPath!).Length / 1024} KB)";
            });
            await Run("下载失败时给出可读原因", async () =>
            {
                // 127.0.0.1:1 没有服务监听 → 连接被拒绝，对应用户截图里的「由于目标计算机积极拒绝」
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    await http.GetAsync("http://127.0.0.1:1/x");
                    throw new Exception("预期连接失败");
                }
                catch (HttpRequestException ex)
                {
                    var msg = TunnelProvider.ExplainNetworkError(ex);
                    Assert(msg.Contains("连接被拒绝") && msg.Contains("导入本地 exe"), "提示不对：" + msg);
                    return msg;
                }
            });
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
            Assert(await RemoteInfo.CopyToClipboardAsync(text), "写剪贴板失败");
            Assert(System.Windows.Clipboard.GetText() == text, "剪贴板内容不一致");
            return text;
        });

        await Run("剪贴板被其他程序占用时仍能复制且不阻塞", async () =>
        {
            var hold = Win32Clipboard.HoldForTestAsync(800);
            await Task.Delay(100); // 确保对方已经占住
            var sw = Stopwatch.StartNew();
            var copy = RemoteInfo.CopyToClipboardAsync("rdphelper-clipboard-test");
            // 复制在等待期间必须把线程让出来（以前是 Thread.Sleep 硬等，界面「未响应」）
            Assert(!copy.IsCompleted, "剪贴板被占用时复制应异步等待");
            bool ok = await copy;
            await hold;
            Assert(ok, "占用解除后应复制成功");
            Assert(System.Windows.Clipboard.GetText() == "rdphelper-clipboard-test", "剪贴板内容不一致");
            return $"等待 {sw.ElapsedMilliseconds} ms 后成功";
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

        await Run("专业版密钥格式校验（纯函数）", async () =>
        {
            await Task.CompletedTask;
            Assert(EditionUpgrade.NormalizeKey(" vk7jg npht mc97j-m9mpg t3v66t ") == EditionUpgrade.GenericProKey, "规范化失败");
            Assert(EditionUpgrade.NormalizeKey("ABCDE-FGHIJ-KLMNO-PQRST") == null, "24 位应判为无效");
            Assert(EditionUpgrade.NormalizeKey("ABCDE-FGHIJ-KLMNO-PQRST-UVWX!") == null, "非法字符应判为无效");
            bool hasChangepk = File.Exists(Path.Combine(Environment.SystemDirectory, "changepk.exe"));
            return $"OK · changepk.exe 存在={hasChangepk}（不在 CI 上真正执行升级：会改系统版本并重启）";
        });

        await Run("卸载恢复防火墙：自定义端口仍可连接", async () =>
        {
            var script = Path.Combine(AppContext.BaseDirectory, "restore-firewall.ps1");
            Assert(File.Exists(script), "exe 旁边没有 restore-firewall.ps1");
            await RdpService.SetPortAsync(13390);
            Assert(await FirewallService.RuleCountAsync(RemoteKind.Rdp) == 2, "改端口后本程序规则不是 2 条");
            var r = await Shell.RunAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"", 120_000);
            Assert(await FirewallService.RuleCountAsync(RemoteKind.Rdp) == 0, "卸载脚本没有删除本程序规则");
            var allow = await Shell.PowerShellAsync("@(Get-NetFirewallRule -Name 'RdpHelper-Restored-RDP-*' | Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' } | Get-NetFirewallPortFilter | Where-Object LocalPort -eq '13390').Count");
            Assert(allow.Output.Trim().EndsWith("2"), "卸载后没有为 13390 恢复 TCP+UDP 放行规则：" + allow.Output);
            await RdpHandshakeAsync("127.0.0.1", 13390);
            // 重新接管：恢复的普通规则必须被删掉，否则会绕过白名单
            await RdpService.SetPortAsync(RdpService.DefaultPort);
            var left = await Shell.PowerShellAsync("@(Get-NetFirewallRule -Name 'RdpHelper-Restored-*').Count");
            Assert(left.Output.Trim().EndsWith("0"), "重新接管后残留恢复规则：" + left.Output);
            return r.Output.Trim();
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
