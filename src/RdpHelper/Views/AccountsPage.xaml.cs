using System.Windows;
using System.Windows.Controls;
using RdpHelper.Services;

namespace RdpHelper.Views;

public sealed record RemoteAccountRow(RemoteAccount A)
{
    public string Name => A.Name;
    public string StateText => (A.Enabled ? L.T("已启用") : L.T("已禁用")) + (A.IsAdmin ? " · " + L.T("管理员") : "");
    public string Detail => L.F("远程桌面 / SSH 登录名：{0}{1}", AccountService.MachinePrefix, A.Name) + (A.InRdpGroup ? "" : " · " + L.T("不在远程桌面用户组"));
    public string ToggleLabel => A.Enabled ? L.T("禁用") : L.T("启用");
    public string CopyLabel => L.T("复制登录名");
    public string ResetLabel => L.T("重置密码");
    public string DeleteLabel => L.T("删除");
}

public partial class AccountsPage : Page
{
    private CurrentAccount? _me;

    public AccountsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _me = await AccountService.GetCurrentAsync();
        UserText.Text = AccountService.MachinePrefix + _me.User;
        KindText.Text = _me.Kind switch
        {
            AccountKind.Microsoft => L.T("微软账户"),
            AccountKind.Domain => L.T("域账户"),
            _ => L.T("本地账户"),
        };
        AdminBadge.Visibility = _me.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        PasswordlessBar.IsOpen = _me.PasswordlessOnly;

        switch (_me.Kind)
        {
            case AccountKind.Local when _me.HasPassword == false:
                StatusDot.Fill = RemotePage.Orange;
                StatusText.Text = L.T("没有设置密码：Windows 不允许空密码远程登录，远程桌面和 SSH 都会被拒绝");
                break;
            case AccountKind.Local when _me.HasPassword == true:
                StatusDot.Fill = RemotePage.Green;
                StatusText.Text = L.T("已设置密码：远程桌面和 SSH 用这个账户名和密码登录");
                break;
            case AccountKind.Microsoft:
                StatusDot.Fill = RemotePage.Green;
                StatusText.Text = L.T("微软账户：远程登录请用微软账户邮箱和密码（不是 PIN 码）");
                break;
            case AccountKind.Domain:
                StatusDot.Fill = RemotePage.Gray;
                StatusText.Text = L.T("域账户：密码由域管理员管理");
                break;
            default:
                StatusDot.Fill = RemotePage.Gray;
                StatusText.Text = L.T("无法判断是否设置了密码");
                break;
        }

        LocalPasswordCard.Visibility = _me.Kind == AccountKind.Local ? Visibility.Visible : Visibility.Collapsed;
        PasswordCardTitle.Text = _me.HasPassword == false ? L.T("设置登录密码") : L.T("修改登录密码");
        MicrosoftPasswordCard.Visibility = _me.Kind == AccountKind.Microsoft ? Visibility.Visible : Visibility.Collapsed;

        LoadAccounts();
        LoadKeys();
    }

    private void LoadAccounts()
    {
        var rows = AccountService.ListRemoteAccounts().Select(a => new RemoteAccountRow(a)).ToList();
        RemoteAccountList.ItemsSource = rows;
        NoRemoteAccountText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadKeys()
    {
        bool installed = SshService.IsInstalled;
        bool admin = _me?.IsAdmin ?? true;
        SshMissingBar.IsOpen = !installed;
        KeyCard.IsEnabled = installed;
        PasswordAuthCard.IsEnabled = installed;
        KeyFileText.Text = L.F("保存在 {0}", SshKeyService.KeysFile(admin)) + (admin ? " · " + L.T("管理员账户的公钥统一放在这里") : "");
        var keys = SshKeyService.List(admin);
        KeyList.ItemsSource = keys;
        NoKeyText.Visibility = keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordAuthToggle.IsChecked = SshKeyService.PasswordAuthEnabled();
    }

    private async Task Run(Func<Task> action)
    {
        Busy.Visibility = Visibility.Visible;
        IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { Notify.Error(L.T("操作失败"), ex.Message); }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            IsEnabled = true;
            await RefreshAsync();
        }
    }

    // ---------- 当前账户密码 ----------

    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var p = NewPasswordBox.Password;
        if (p.Length == 0) { Notify.Warn(L.T("密码不能为空")); return; }
        if (p != ConfirmPasswordBox.Password) { Notify.Warn(L.T("两次输入的密码不一致")); return; }
        if (_me == null) return;
        await Run(async () =>
        {
            await Task.Run(() => AccountService.SetPassword(_me.User, p));
            AccountService.NotePasswordSet();
            NewPasswordBox.Password = ConfirmPasswordBox.Password = "";
            Notify.Success(L.T("密码已设置"), L.F("远程登录时用户名填 {0}，密码填刚才设置的密码；下次开机也需要输入它。", AccountService.MachinePrefix + _me.User));
        });
    }

    private void MsPassword_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(AccountService.MicrosoftPasswordUrl);
    private void SignInOptions_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(AccountService.SignInOptionsUri);

    // ---------- 远程专用账户 ----------

    private async void CreateUser_Click(object sender, RoutedEventArgs e)
    {
        var name = NewUserBox.Text.Trim();
        if (AccountService.ValidateUserName(name) is { } err) { Notify.Warn(L.T("用户名不可用"), err); return; }
        var p = NewUserPasswordBox.Password;
        if (p.Length == 0) { Notify.Warn(L.T("密码不能为空"), L.T("Windows 不允许空密码远程登录")); return; }
        if (p != NewUserConfirmBox.Password) { Notify.Warn(L.T("两次输入的密码不一致")); return; }
        bool admin = NewUserAdminBox.IsChecked == true;
        await Run(async () =>
        {
            await Task.Run(() => AccountService.CreateRemoteAccount(name, p, admin));
            NewUserBox.Text = "";
            NewUserPasswordBox.Password = NewUserConfirmBox.Password = "";
            NewUserAdminBox.IsChecked = false;
            Notify.Success(L.T("远程账户已创建"), L.F("对方用 {0} 和你设置的密码登录远程桌面 / SSH", AccountService.MachinePrefix + name));
        });
    }

    private static string? TagOf(object sender) => (sender as FrameworkElement)?.Tag as string;

    private async void CopyLogin_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is { } n && await RemoteInfo.CopyToClipboardAsync(AccountService.MachinePrefix + n))
            Notify.Success(L.T("已复制"), AccountService.MachinePrefix + n);
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } n) return;
        var p = PasswordPrompt.Ask(Window.GetWindow(this), L.F("重置 {0} 的密码", n));
        if (p == null) return;
        await Run(async () =>
        {
            await Task.Run(() => AccountService.SetPassword(n, p));
            Notify.Success(L.T("密码已重置"));
        });
    }

    private async void ToggleUser_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } n) return;
        var acc = AccountService.ListRemoteAccounts().FirstOrDefault(a => a.Name == n);
        if (acc == null) return;
        await Run(async () =>
        {
            await Task.Run(() => AccountService.SetEnabled(n, !acc.Enabled));
            Notify.Success(acc.Enabled ? L.F("已禁用 {0}", n) : L.F("已启用 {0}", n));
        });
    }

    private async void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } n) return;
        if (!RemotePage.Confirm(L.F("确定删除远程账户 {0} 吗？它的桌面文件和设置会一起删除，无法恢复。", n))) return;
        await Run(async () =>
        {
            await AccountService.DeleteRemoteAccountAsync(n);
            Notify.Success(L.F("已删除 {0}", n));
        });
    }

    // ---------- SSH 密钥 ----------

    private async void CopyKeygen_Click(object sender, RoutedEventArgs e)
    {
        if (await RemoteInfo.CopyToClipboardAsync("ssh-keygen -t ed25519")) Notify.Success(L.T("已复制"), "ssh-keygen -t ed25519");
    }

    /// <summary>读取 .pub 文件内容回填到输入框（追加，可一次选多个）。公钥文件很小，超过 64 KB 的视为选错了文件。</summary>
    private void FillFromFiles(IEnumerable<string> files)
    {
        var added = new List<string>();
        foreach (var f in files)
        {
            try
            {
                if (new FileInfo(f).Length > 64 * 1024) { Notify.Warn(L.T("文件太大，不像公钥"), Path.GetFileName(f)); continue; }
                var text = File.ReadAllText(f).Trim();
                var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
                if (lines.Count == 0 || lines.Any(l => SshKeyService.Parse(l) == null))
                {
                    Notify.Warn(L.T("不是有效的 SSH 公钥文件"),
                        text.Contains("PRIVATE KEY") ? L.T("这是私钥！请选择 .pub 结尾的公钥文件，私钥千万不要发给别人。") : Path.GetFileName(f));
                    continue;
                }
                added.AddRange(lines);
            }
            catch (Exception ex) { Notify.Error(L.T("读取文件失败"), ex.Message); }
        }
        if (added.Count == 0) return;
        var current = KeyInputBox.Text.TrimEnd();
        KeyInputBox.Text = (current.Length > 0 ? current + Environment.NewLine : "") + string.Join(Environment.NewLine, added);
        KeyInputBox.CaretIndex = KeyInputBox.Text.Length;
        Notify.Info(L.F("已读取 {0} 把公钥", added.Count), L.T("确认无误后点「添加公钥」"));
    }

    private void PickKeyFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("选择 SSH 公钥文件"),
            Filter = L.T("SSH 公钥") + " (*.pub)|*.pub|" + L.T("所有文件") + " (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"))
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog() == true) FillFromFiles(dlg.FileNames);
    }

    private void KeyInput_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void KeyInput_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            FillFromFiles(files);
        }
    }

    private async void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var text = KeyInputBox.Text;
        await Run(async () =>
        {
            int n = await SshKeyService.AddAsync(_me?.IsAdmin ?? true, text);
            KeyInputBox.Text = "";
            if (n > 0) Notify.Success(L.F("已添加 {0} 把公钥", n), L.T("对方现在可以免密码 SSH 登录当前账户"));
            else Notify.Info(L.T("这把公钥已经存在"));
        });
    }

    private async void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } fp) return;
        if (!RemotePage.Confirm(L.F("删除公钥 {0}？对方将无法再用它登录。", fp))) return;
        await Run(async () =>
        {
            await SshKeyService.RemoveAsync(_me?.IsAdmin ?? true, fp);
            Notify.Success(L.T("公钥已删除"));
        });
    }

    private async void PasswordAuth_Click(object sender, RoutedEventArgs e)
    {
        bool want = PasswordAuthToggle.IsChecked == true;
        await Run(async () =>
        {
            var warn = await SshKeyService.SetPasswordAuthAsync(want, _me?.IsAdmin ?? true);
            RemotePage.Done(want ? L.T("已允许 SSH 密码登录") : L.T("已禁止 SSH 密码登录，只能用公钥登录"), warn);
        });
    }
}
