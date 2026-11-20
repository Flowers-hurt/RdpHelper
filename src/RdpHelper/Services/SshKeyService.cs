using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RdpHelper.Services;

public sealed record SshKey(string Type, string Blob, string Comment, string Line)
{
    /// <summary>与 ssh-keygen -lf 一致的 SHA256 指纹。</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(Blob))).TrimEnd('=');

    public string ShortType => Type switch
    {
        "ssh-ed25519" => "ED25519",
        "ssh-rsa" => "RSA",
        _ when Type.StartsWith("ecdsa") => "ECDSA",
        _ when Type.StartsWith("sk-") => "FIDO2",
        _ => Type,
    };

    public string Display => string.IsNullOrWhiteSpace(Comment) ? ShortType : $"{Comment} · {ShortType}";
}

/// <summary>
/// SSH 公钥登录（免密码）：管理当前账户的 authorized_keys，以及「禁止密码登录」开关。
/// Windows OpenSSH 规则：管理员组成员的公钥必须放在 %ProgramData%\ssh\administrators_authorized_keys
/// （且只允许 Administrators / SYSTEM 访问），普通用户放在 %USERPROFILE%\.ssh\authorized_keys。
/// </summary>
public static partial class SshKeyService
{
    public static string AdminKeysFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "administrators_authorized_keys");

    public static string UserKeysFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys");

    public static string KeysFile(bool isAdmin) => isAdmin ? AdminKeysFile : UserKeysFile;

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(?:256|384|521)|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com)\s+([A-Za-z0-9+/]+={0,3})(?:\s+(.*))?$")]
    private static partial Regex KeyLine();

    /// <summary>解析一行公钥；不合法返回 null（同时校验 base64 内容里的类型名与前缀一致）。</summary>
    public static SshKey? Parse(string line)
    {
        line = line.Trim();
        var m = KeyLine().Match(line);
        if (!m.Success) return null;
        try
        {
            var blob = Convert.FromBase64String(m.Groups[2].Value);
            if (blob.Length < 4) return null;
            int len = blob[0] << 24 | blob[1] << 16 | blob[2] << 8 | blob[3];
            if (len <= 0 || 4 + len > blob.Length || Encoding.ASCII.GetString(blob, 4, len) != m.Groups[1].Value) return null;
        }
        catch (FormatException) { return null; }
        return new SshKey(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim(), line);
    }

    public static List<SshKey> List(bool isAdmin)
    {
        var f = KeysFile(isAdmin);
        if (!File.Exists(f)) return new();
        return File.ReadAllLines(f).Select(Parse).Where(k => k != null).Select(k => k!).ToList();
    }

    /// <summary>添加公钥（可一次粘贴多行），返回新增数量；重复的自动跳过。</summary>
    public static async Task<int> AddAsync(bool isAdmin, string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count == 0) throw new ArgumentException(L.T("请粘贴公钥内容"));
        var keys = new List<SshKey>();
        foreach (var l in lines)
            keys.Add(Parse(l) ?? throw new ArgumentException(L.F("不是有效的 SSH 公钥：{0}", l.Length > 40 ? l[..40] + "…" : l)));

        var existing = List(isAdmin);
        var add = keys.Where(k => existing.All(e => e.Blob != k.Blob)).GroupBy(k => k.Blob).Select(g => g.First()).ToList();
        if (add.Count == 0) return 0;
        await WriteAsync(isAdmin, existing.Concat(add));
        return add.Count;
    }

    public static async Task RemoveAsync(bool isAdmin, string fingerprint)
    {
        var keys = List(isAdmin);
        await WriteAsync(isAdmin, keys.Where(k => k.Fingerprint != fingerprint));
    }

    /// <summary>
    /// 写文件：UTF-8 无 BOM、LF 换行（sshd 不认 BOM）。保留文件里原有的注释和无法识别的行，
    /// 管理员密钥文件要收紧权限，否则 sshd 会拒绝使用。
    /// </summary>
    private static async Task WriteAsync(bool isAdmin, IEnumerable<SshKey> keys)
    {
        var f = KeysFile(isAdmin);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var keep = File.Exists(f) ? File.ReadAllLines(f).Where(l => Parse(l) == null && l.Trim().Length > 0).ToList() : new List<string>();
        var content = string.Join("\n", keep.Concat(keys.Select(k => k.Line))) + "\n";
        await File.WriteAllTextAsync(f, content, new UTF8Encoding(false));
        if (isAdmin)
            await Shell.RunAsync("icacls.exe", $"\"{f}\" /inheritance:r /grant *S-1-5-32-544:F /grant *S-1-5-18:F", 30_000);
    }

    // ---------------- 禁止密码登录 ----------------

    [GeneratedRegex(@"^\s*PasswordAuthentication\s+(yes|no)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PasswordAuthLine();

    /// <summary>sshd_config 中是否允许密码登录（默认 yes）。只看第一个 Match 块之前的全局设置。</summary>
    public static bool PasswordAuthEnabled()
    {
        var path = SshService.ConfigPath;
        if (!File.Exists(path)) return true;
        var global = GlobalSection(File.ReadAllText(path));
        var m = PasswordAuthLine().Match(global);
        return !m.Success || m.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string GlobalSection(string config)
    {
        var idx = Regex.Match(config, @"^\s*Match\s", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return idx.Success ? config[..idx.Index] : config;
    }

    /// <summary>纯函数：在全局段写入 PasswordAuthentication yes/no（删掉全局段已有的设置，插在注释行之后或开头）。</summary>
    public static string RewritePasswordAuth(string config, bool enabled)
    {
        var text = config.Replace("\r\n", "\n");
        var match = Regex.Match(text, @"^\s*Match\s", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        string global = match.Success ? text[..match.Index] : text, rest = match.Success ? text[match.Index..] : "";
        var lines = global.Split('\n').ToList();
        lines.RemoveAll(l => PasswordAuthLine().IsMatch(l));
        int idx = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*#\s*PasswordAuthentication\s", RegexOptions.IgnoreCase));
        var newLine = "PasswordAuthentication " + (enabled ? "yes" : "no");
        if (idx >= 0) lines.Insert(idx + 1, newLine); else lines.Insert(0, newLine);
        return (string.Join("\n", lines) + rest).Replace("\n", "\r\n");
    }

    /// <summary>修改密码登录开关并重启 sshd。关闭密码登录前必须至少有一把公钥，防止把自己锁在外面。</summary>
    public static async Task<string> SetPasswordAuthAsync(bool enabled, bool isAdmin)
    {
        if (!SshService.IsInstalled) throw new InvalidOperationException(L.T("请先安装 OpenSSH 服务器"));
        if (!enabled && List(isAdmin).Count == 0) throw new InvalidOperationException(L.T("请先添加至少一把公钥，再关闭密码登录，否则将无法通过 SSH 登录"));
        var path = SshService.ConfigPath;
        if (!File.Exists(path)) await Shell.PowerShellAsync("Start-Service sshd -ErrorAction SilentlyContinue; Start-Sleep 2");
        string text = File.Exists(path) ? File.ReadAllText(path) : "";
        File.WriteAllText(path, RewritePasswordAuth(text, enabled), new UTF8Encoding(false));
        if (!await SshService.IsRunningAsync()) return "";
        var r = await Shell.PowerShellAsync("try { Restart-Service sshd -Force -ErrorAction Stop } catch { Write-Output ('WARN:重启 sshd 失败：' + $_.Exception.Message) }");
        return RdpService.CollectWarnings(r);
    }
}
