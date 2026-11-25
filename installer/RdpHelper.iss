; Inno Setup 安装脚本 —— 由 GitHub Actions 调用：ISCC /DAppVersion=x.y.z installer\RdpHelper.iss
; 安装包包含主程序 + 微软官方 Win32-OpenSSH 安装包（redist，用于离线安装 SSH 服务器）；
; 不包含任何内网穿透组件（cloudflared / ngrok / bore 在程序内按需下载）。

#ifndef AppVersion
  #define AppVersion "1.2.6"
#endif
#define AppName "RdpHelper"
#define AppDisplayName "RdpHelper 远程桌面助手"
#define AppExe "RdpHelper.exe"

[Setup]
AppId={{6B0F1C2E-6E4B-4E4A-9E3C-52D7B2A1F0C8}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
AppPublisher=RdpHelper contributors
AppPublisherURL=https://github.com/Flowers-hurt/RdpHelper
AppSupportURL=https://github.com/Flowers-hurt/RdpHelper/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=RdpHelper-Setup-{#AppVersion}-x64
SetupIconFile=..\src\RdpHelper\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE

[Languages]
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\redist\*"; DestDir: "{app}\redist"; Flags: ignoreversion
Source: "restore-firewall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppDisplayName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppDisplayName}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; 卸载时删除本程序的防火墙规则，并按当前端口恢复普通放行规则（不改动远程桌面 / SSH 的开关状态，卸载后照样能连）
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\restore-firewall.ps1"""; Flags: runhidden; RunOnceId: "RestoreFwRules"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\RdpHelper\tools"
