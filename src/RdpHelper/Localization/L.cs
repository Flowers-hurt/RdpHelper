using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Windows.Markup;

namespace RdpHelper.Localization;

/// <summary>
/// 极简 gettext 式本地化：源码里直接写中文原文作为 key，英文译文放在 Localization/en.json（嵌入资源）。
///   C#：   L.T("远程桌面")、L.F("端口 {0}", port)
///   XAML： Text="{l:T '远程桌面'}"
/// 找不到译文时回退为中文原文，并记入 <see cref="Missing"/>，界面自检会据此报错。
/// 切换语言时重建主窗口（见 App.ReloadUi），所以不需要做实时绑定。
/// </summary>
public static class L
{
    public const string Zh = "zh";
    public const string En = "en";

    /// <summary>当前生效的语言：zh / en。</summary>
    public static string Lang { get; private set; } = Zh;
    public static bool IsEnglish => Lang == En;

    private static readonly Lazy<Dictionary<string, string>> EnTable = new(LoadEn);

    /// <summary>运行中遇到的、英文表里缺失的原文。</summary>
    public static ConcurrentDictionary<string, byte> Missing { get; } = new();

    public static IReadOnlyDictionary<string, string> English => EnTable.Value;

    /// <summary>setting: system / zh / en。system 时看系统界面语言，中文系统用中文，其余用英文。</summary>
    public static string Resolve(string? setting) => setting switch
    {
        Zh => Zh,
        En => En,
        _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Zh : En,
    };

    public static void Apply(string? setting)
    {
        Lang = Resolve(setting);
        var culture = CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "zh-CN");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    public static string T(string zh)
    {
        if (!IsEnglish || string.IsNullOrEmpty(zh)) return zh;
        if (EnTable.Value.TryGetValue(zh, out var en)) return en;
        Missing.TryAdd(zh, 0);
        return zh;
    }

    /// <summary>只做「需要翻译」的标记、原样返回（给 PowerShell 脚本里输出的中文前缀登记 key）。</summary>
    public static string N(string zh) => zh;

    public static string F(string zh, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(zh), args);

    private static Dictionary<string, string> LoadEn()
    {
        using var s = typeof(L).Assembly.GetManifestResourceStream("RdpHelper.Localization.en.json")
                      ?? throw new InvalidOperationException("en.json resource missing");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
    }
}

/// <summary>XAML 用：{l:T '中文原文'}</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
