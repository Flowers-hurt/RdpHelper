using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RdpHelper.Services;

/// <summary>
/// 家庭版 → 专业版（就地升级，不重装、保留文件和软件）。
/// 只走正规渠道：微软商店购买 / 用户自己的专业版密钥 / 微软公开的专业版通用安装密钥（仅切换版本，
/// 有数字许可证时联网自动激活，否则保持未激活）。不包含任何绕过激活的手段。
/// </summary>
public static partial class EditionUpgrade
{
    /// <summary>微软商店「Windows 10/11 专业版升级」商品页。</summary>
    public const string StoreUri = "ms-windows-store://pdp/?productid=DG7GMGF0D8H4";

    public const string ActivationSettingsUri = "ms-settings:activation";

    /// <summary>微软官方公布的 Windows 10/11 专业版通用安装密钥（Generic / Default key），不能激活系统。</summary>
    public const string GenericProKey = "VK7JG-NPHTM-C97JM-9MPGT-3V66T";

    [GeneratedRegex("^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$")]
    private static partial Regex KeyRegex();

    /// <summary>规范化用户输入：去空格、转大写、补连字符；格式不对返回 null。</summary>
    public static string? NormalizeKey(string input)
    {
        var s = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length != 25) return null;
        var key = string.Join("-", Enumerable.Range(0, 5).Select(i => s.Substring(i * 5, 5)));
        return KeyRegex().IsMatch(key) ? key : null;
    }

    public static void OpenStore() => Open(StoreUri);
    public static void OpenActivationSettings() => Open(ActivationSettingsUri);

    private static void Open(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    /// <summary>调用系统自带的 changepk.exe 更换产品密钥并升级版本；成功时系统会提示并自动重启。</summary>
    public static Task<Shell.Result> UpgradeAsync(string key)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "changepk.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException(L.T("系统里找不到 changepk.exe，请改用「打开系统激活设置」手动输入密钥。"));
        return Shell.RunAsync(exe, "/ProductKey " + key, 10 * 60_000);
    }
}
