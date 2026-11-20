using System.Text.Json;

namespace RdpHelper.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpHelper");

    /// <summary>内网穿透组件下载目录（不随安装包分发，用户按需下载）。</summary>
    public static string Tools => Path.Combine(Root, "tools");

    /// <summary>黑白名单是机器级配置，放 ProgramData，换用户登录也一致。</summary>
    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RdpHelper", "settings.json");

    public static string Version =>
        typeof(AppPaths).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}

public sealed class AppSettings
{
    /// <summary>system / light / dark</summary>
    public string Theme { get; set; } = "system";

    /// <summary>system / zh / en</summary>
    public string Language { get; set; } = "system";

    /// <summary>acrylic / mica / tabbed / none</summary>
    public string Backdrop { get; set; } = "acrylic";

    /// <summary>关闭主窗口时：tray（收进托盘，后台继续运行）/ exit（退出程序）</summary>
    public string CloseAction { get; set; } = "tray";

    public List<string> RdpAllow { get; set; } = new();
    public List<string> RdpDeny { get; set; } = new();
    public List<string> SshAllow { get; set; } = new();
    public List<string> SshDeny { get; set; } = new();

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { /* 损坏就用默认 */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 忽略 */ }
    }
}
