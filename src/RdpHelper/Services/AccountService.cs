using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace RdpHelper.Services;

public enum AccountKind { Local, Microsoft, Domain }

/// <summary>当前登录账户的状态。HasPassword 为 null 表示无法判断（微软账户 / 域账户）。</summary>
public sealed record CurrentAccount(string User, AccountKind Kind, bool? HasPassword, bool IsAdmin, bool PasswordlessOnly)
{
    /// <summary>远程桌面 / SSH 会因为没有密码而无法登录。</summary>
    public bool BlankPasswordProblem => Kind == AccountKind.Local && HasPassword == false;
}

public sealed record RemoteAccount(string Name, bool Enabled, bool IsAdmin, bool InRdpGroup, string Comment);

/// <summary>
/// 远程登录相关的 Windows 账户操作。远程桌面和 SSH 都用 Windows 账户登录（系统机制，无法另设「远程密码」），
/// 所以这里提供：检测空密码 / 微软账户、给当前本地账户设置密码、创建「远程专用账户」。
/// 全部走 netapi32 / advapi32 原生 API，密码不会出现在任何进程的命令行参数里。
/// </summary>
public static class AccountService
{
    /// <summary>本程序创建的账户写在「描述」里的标记，用来区分其它账户。</summary>
    public const string Marker = "Created by RdpHelper (remote access)";

    public static string MachinePrefix => Environment.MachineName + "\\";

    private static CurrentAccount? _cached;
    private static bool _checkedOnce;

    /// <summary>在本程序里给当前账户设置了密码后调用，避免再次用空密码试探。</summary>
    public static void NotePasswordSet()
    {
        if (_cached != null) _cached = _cached with { HasPassword = true };
    }

    /// <summary>
    /// 读取当前账户状态。判断是否有密码需要用空密码尝试一次网络登录：
    /// 有密码时这会记一次失败登录（计入锁定计数，10 分钟后清零），所以每次程序运行只检测一次。
    /// </summary>
    public static async Task<CurrentAccount> GetCurrentAsync(bool refresh = false)
    {
        if (_cached != null && !refresh) return _cached;
        string user = Environment.UserName;
        var kind = AccountKind.Local;
        if (!string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            kind = AccountKind.Domain;
        else
        {
            var r = await Shell.PowerShellAsync($"(Get-LocalUser -Name '{user.Replace("'", "''")}' -ErrorAction SilentlyContinue).PrincipalSource");
            if (r.Output.Contains("MicrosoftAccount", StringComparison.OrdinalIgnoreCase)) kind = AccountKind.Microsoft;
        }

        // 已经检测过就复用（避免反复记失败登录）；设置过密码后由 NotePasswordSet 更新
        bool? hasPassword = _cached?.HasPassword;
        if (kind == AccountKind.Local && hasPassword == null && !_checkedOnce)
        {
            _checkedOnce = true;
            hasPassword = await Task.Run(() => HasPassword(user));
        }

        bool admin = kind != AccountKind.Domain ? IsLocalAdmin(user) : new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        _cached = new CurrentAccount(user, kind, hasPassword, admin, IsPasswordlessOnly());
        return _cached;
    }

    /// <summary>
    /// 用空密码尝试网络登录：成功 = 空密码且系统允许远程空密码；1327（账户限制）= 空密码被策略拦截；1326 = 有密码。
    /// </summary>
    public static bool? HasPassword(string user)
    {
        if (LogonUser(user, ".", "", LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out var token))
        {
            CloseHandle(token);
            return false;
        }
        return Marshal.GetLastWin32Error() switch
        {
            1327 => false,  // ERROR_ACCOUNT_RESTRICTION：空密码只能在本机控制台登录
            1326 => true,   // ERROR_LOGON_FAILURE：密码不对 → 有密码
            1331 => null,   // 账户已禁用
            _ => null,
        };
    }

    /// <summary>验证用户名和密码能否通过网络登录（远程桌面 / SSH 走的就是这种登录）。</summary>
    public static bool TestNetworkLogon(string user, string password)
    {
        if (!LogonUser(user, ".", password, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out var token)) return false;
        CloseHandle(token);
        return true;
    }

    /// <summary>Windows 11「为了提高安全性，仅允许对此设备上的微软账户使用 Windows Hello 登录」——开启时远程桌面输入密码会失败。</summary>
    public static bool IsPasswordlessOnly()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device");
        return k?.GetValue("DevicePasswordLessBuildVersion") is int v && v == 2;
    }

    public const string SignInOptionsUri = "ms-settings:signinoptions";
    public const string MicrosoftPasswordUrl = "https://account.live.com/password/change";

    // ---------------- 设置密码 ----------------

    /// <summary>给本地账户设置新密码（管理员权限，无需旧密码）。</summary>
    public static void SetPassword(string user, string password)
    {
        var info = new USER_INFO_1003 { usri1003_password = password };
        Check(NetUserSetInfo(null, user, 1003, ref info, out _));
    }

    // ---------------- 远程专用账户 ----------------

    private static string GroupName(string sid) =>
        new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value.Split('\\').Last();

    /// <summary>「Remote Desktop Users」组（名字随系统语言变化，按 SID 查）。</summary>
    public static string RdpUsersGroup => GroupName("S-1-5-32-555");
    public static string AdministratorsGroup => GroupName("S-1-5-32-544");

    public static string? ValidateUserName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 20) return L.T("用户名需为 1–20 个字符");
        if (name.IndexOfAny("\"/\\[]:;|=,+*?<>@".ToCharArray()) >= 0 || name.All(c => c == '.' || c == ' '))
            return L.T("用户名不能包含 \" / \\ [ ] : ; | = , + * ? < > @");
        if (string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return L.T("用户名不能和计算机名相同");
        return null;
    }

    public static void CreateRemoteAccount(string name, string password, bool admin)
    {
        name = name.Trim();
        var info = new USER_INFO_1
        {
            usri1_name = name,
            usri1_password = password,
            usri1_priv = USER_PRIV_USER,
            usri1_comment = Marker,
            usri1_flags = UF_SCRIPT | UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWD,
        };
        Check(NetUserAdd(null, 1, ref info, out _));
        try
        {
            AddToGroup(RdpUsersGroup, name);
            if (admin) AddToGroup(AdministratorsGroup, name);
        }
        catch
        {
            NetUserDel(null, name);
            throw;
        }
    }

    public static List<RemoteAccount> ListRemoteAccounts()
    {
        var list = new List<RemoteAccount>();
        int resume = 0;
        int rc = NetUserEnum(null, 1, FILTER_NORMAL_ACCOUNT, out var buf, MAX_PREFERRED_LENGTH, out int read, out _, ref resume);
        if (rc != 0 && rc != ERROR_MORE_DATA) return list;
        try
        {
            int size = Marshal.SizeOf<USER_INFO_1>();
            for (int i = 0; i < read; i++)
            {
                var u = Marshal.PtrToStructure<USER_INFO_1>(buf + i * size);
                if (u.usri1_comment != Marker) continue;
                var groups = LocalGroups(u.usri1_name);
                list.Add(new RemoteAccount(u.usri1_name, (u.usri1_flags & UF_ACCOUNTDISABLE) == 0,
                    groups.Contains(AdministratorsGroup, StringComparer.OrdinalIgnoreCase),
                    groups.Contains(RdpUsersGroup, StringComparer.OrdinalIgnoreCase), u.usri1_comment));
            }
        }
        finally { NetApiBufferFree(buf); }
        return list.OrderBy(a => a.Name).ToList();
    }

    public static void SetEnabled(string name, bool enabled)
    {
        Check(NetUserGetInfo(null, name, 1, out var buf));
        int flags;
        try { flags = Marshal.PtrToStructure<USER_INFO_1>(buf).usri1_flags; }
        finally { NetApiBufferFree(buf); }
        flags = enabled ? flags & ~UF_ACCOUNTDISABLE : flags | UF_ACCOUNTDISABLE;
        var info = new USER_INFO_1008 { usri1008_flags = flags };
        Check(NetUserSetInfo(null, name, 1008, ref info, out _));
    }

    /// <summary>删除账户，并删除它的用户配置文件目录（如果登录过）。只允许删本程序创建的账户。</summary>
    public static async Task DeleteRemoteAccountAsync(string name)
    {
        if (!ListRemoteAccounts().Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L.T("只能删除由 RdpHelper 创建的远程账户"));
        string? sid = null;
        try { sid = new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier)).Value; } catch { /* ignore */ }
        Check(NetUserDel(null, name));
        if (sid != null)
            await Shell.PowerShellAsync($"Get-CimInstance Win32_UserProfile -Filter \"SID='{sid}'\" -ErrorAction SilentlyContinue | Remove-CimInstance -ErrorAction SilentlyContinue", 120_000);
    }

    private static void AddToGroup(string group, string user)
    {
        var m = new LOCALGROUP_MEMBERS_INFO_3 { lgrmi3_domainandname = MachinePrefix + user };
        int rc = NetLocalGroupAddMembers(null, group, 3, ref m, 1);
        if (rc != 0 && rc != ERROR_MEMBER_IN_ALIAS) Check(rc);
    }

    private static List<string> LocalGroups(string user)
    {
        var list = new List<string>();
        if (NetUserGetLocalGroups(null, user, 0, 0, out var buf, MAX_PREFERRED_LENGTH, out int read, out _) != 0) return list;
        try
        {
            int size = Marshal.SizeOf<LOCALGROUP_USERS_INFO_0>();
            for (int i = 0; i < read; i++) list.Add(Marshal.PtrToStructure<LOCALGROUP_USERS_INFO_0>(buf + i * size).lgrui0_name);
        }
        finally { NetApiBufferFree(buf); }
        return list;
    }

    private static bool IsLocalAdmin(string user)
    {
        try { return LocalGroups(user).Contains(AdministratorsGroup, StringComparer.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void Check(int rc)
    {
        if (rc == 0) return;
        throw new InvalidOperationException(rc switch
        {
            2224 => L.T("这个用户名已经存在"),
            2221 => L.T("找不到这个用户"),
            2245 => L.T("密码不符合系统密码策略（长度、复杂度或与历史密码重复），请换一个更复杂的密码"),
            2226 => L.T("此操作只能在域控制器上进行"),
            5 => L.T("没有权限（需要管理员身份运行）"),
            1378 => L.T("账户已经在该组中"),
            _ => L.F("Windows 返回错误 {0}", rc),
        });
    }

    // ---------------- P/Invoke ----------------

    private const int LOGON32_LOGON_NETWORK = 3, LOGON32_PROVIDER_DEFAULT = 0;
    private const int USER_PRIV_USER = 1;
    private const int UF_SCRIPT = 0x1, UF_ACCOUNTDISABLE = 0x2, UF_NORMAL_ACCOUNT = 0x200, UF_DONT_EXPIRE_PASSWD = 0x10000;
    private const int FILTER_NORMAL_ACCOUNT = 2, MAX_PREFERRED_LENGTH = -1, ERROR_MORE_DATA = 234, ERROR_MEMBER_IN_ALIAS = 1378;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1
    {
        public string usri1_name;
        public string usri1_password;
        public int usri1_password_age;
        public int usri1_priv;
        public string? usri1_home_dir;
        public string usri1_comment;
        public int usri1_flags;
        public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1003 { public string usri1003_password; }

    [StructLayout(LayoutKind.Sequential)]
    private struct USER_INFO_1008 { public int usri1008_flags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOCALGROUP_MEMBERS_INFO_3 { public string lgrmi3_domainandname; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOCALGROUP_USERS_INFO_0 { public string lgrui0_name; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string domain, string password, int type, int provider, out IntPtr token);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(string? server, int level, ref USER_INFO_1 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? server, string user, int level, ref USER_INFO_1003 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? server, string user, int level, ref USER_INFO_1008 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buf);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(string? server, int level, int filter, out IntPtr buf, int prefMax, out int read, out int total, ref int resume);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(string? server, string user);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(string? server, string group, int level, ref LOCALGROUP_MEMBERS_INFO_3 buf, int total);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? server, string user, int level, int flags, out IntPtr buf, int prefMax, out int read, out int total);

    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buf);
}
