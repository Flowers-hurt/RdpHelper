<div align="center">

<img src="src/RdpHelper/Assets/app.png" width="120" alt="RdpHelper logo" />

# RdpHelper 远程桌面助手

**一键开启 Windows 远程桌面 / OpenSSH · 自定义端口 · IP 黑白名单 · 按需内网穿透**

Win11 设置风格 · 亚克力毛玻璃 · 单文件免依赖

[![Build](https://img.shields.io/github/actions/workflow/status/Flowers-hurt/RdpHelper/build.yml?branch=main&style=flat-square&logo=githubactions&logoColor=white&label=build)](https://github.com/Flowers-hurt/RdpHelper/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Flowers-hurt/RdpHelper?style=flat-square&logo=github&color=2ea44f)](https://github.com/Flowers-hurt/RdpHelper/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Flowers-hurt/RdpHelper/total?style=flat-square&color=0078d4)](https://github.com/Flowers-hurt/RdpHelper/releases)
[![License](https://img.shields.io/github/license/Flowers-hurt/RdpHelper?style=flat-square&color=yellow)](LICENSE)
<br/>
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20%7C%20Server-0078D6?style=flat-square&logo=windows&logoColor=white)](#-系统要求)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WPF-UI](https://img.shields.io/badge/UI-WPF--UI%20Fluent-5C2D91?style=flat-square)](https://github.com/lepoco/wpfui)
[![Self-test](https://img.shields.io/badge/self--test-28%20passed-brightgreen?style=flat-square)](#-自动化测试)
[![Stars](https://img.shields.io/github/stars/Flowers-hurt/RdpHelper?style=flat-square&logo=github)](https://github.com/Flowers-hurt/RdpHelper/stargazers)

**简体中文** | [English](README.en.md)

[下载](#-下载与安装) · [功能](#-功能一览) · [截图](#-界面截图) · [原理](#-工作原理) · [内网穿透](#-内网穿透) · [常见问题](#-常见问题)

</div>

---

## 📖 简介

想让同事 / 家人 / 自己的另一台电脑远程进来，在 Windows 上通常要：打开设置找远程桌面 → 折腾防火墙 → 改注册表换端口 → 查 IP → 发现不在一个局域网还得找穿透工具……

**RdpHelper** 把这些步骤做成了一个仿 Windows 11「设置」风格的小工具：**一个开关开启远程桌面或 SSH，一个按钮改端口 / 恢复默认，一个按钮把连接信息复制给别人**。需要公网访问时，再从 Cloudflare / ngrok / bore 里挑一个，程序按需下载并自动拿到公网地址。

> [!NOTE]
> 安装包 **不内置任何内网穿透程序**。穿透组件只在你点「下载组件」时从各自官方地址获取，保持安装包干净、可审计。

## ✨ 功能一览

| 模块 | 功能 | 说明 |
| :-- | :-- | :-- |
| 🖥️ **远程桌面** | 一键开启 / 关闭 | 写 `fDenyTSConnections`，自动放行防火墙、拉起 `TermService` |
| | 网络级别身份验证 (NLA) | 一键开关，默认推荐开启 |
| | 自定义端口 / 随机端口 / 恢复 3389 | 写 `PortNumber` → 更新防火墙（TCP+UDP）→ 安全重启服务 |
| | 显示局域网 IP | 自动识别以太网 / Wi-Fi，标记虚拟网卡与推荐地址 |
| | 一键复制远程信息 | 计算机名、用户名、局域网 / 公网地址、连接方法（**不含密码**） |
| 🐚 **SSH** | 一键安装 OpenSSH 服务器 | **安装包自带**微软官方 Win32-OpenSSH，离线十几秒装好；失败再走「Windows 可选功能」/ 在线下载；进度条 + 安装日志，切换页面不丢进度 |
| | 开启 / 关闭 sshd | 同时设置开机自启与防火墙 |
| | 自定义端口 / 恢复 22 | 改写 `sshd_config` 的 `Port` 并重启 sshd |
| 🛡️ **访问控制** | IP 白名单 | 留空 = 允许所有；填写后仅名单内可连 |
| | IP 黑名单 | 始终优先拦截（防火墙 Block 优先于 Allow） |
| | 支持格式 | 单 IP、IPv6、CIDR `192.168.1.0/24`、范围 `10.0.0.1-10.0.0.50`、`LocalSubnet` |
| 🌐 **内网穿透** | Cloudflare Tunnel | 免注册、免费；内置「连接别人的电脑」访问端 |
| | ngrok | Authtoken 一键写入，得到 `tcp://x.tcp.ngrok.io:端口` |
| | bore | 开源极简，公共服务器 `bore.pub`，免注册 |
| | 远程桌面 / SSH 均可穿透 | 子进程挂在 Job Object 上，程序退出必定一起结束 |
| 🎨 **界面** | Win11 Fluent 设计 | 仿系统「设置」，导航 + 卡片 + 开关 |
| | 毛玻璃 | 亚克力 / 云母 / 云母 Alt 可切换；浅色 / 深色 / 跟随系统 |
| | 🌐 中英双语 | 设置里切换「跟随系统 / 简体中文 / English」，立即生效；复制给别人的连接信息也随语言输出 |

## 📸 界面截图

> 截图由 CI 在真实 Windows 上运行程序后以 **2 倍分辨率** 自动渲染（`--gui-selftest`），所示状态均为真实数据。CI 虚拟机没有 Win11 的亚克力合成，窗口背后的毛玻璃效果为合成示意。

<table>
  <tr>
    <td align="center"><b>远程桌面 · 深色</b></td>
    <td align="center"><b>远程桌面 · 浅色</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/remote-dark.png" alt="远程桌面 深色" /></td>
    <td><img src="docs/screenshots/remote-light.png" alt="远程桌面 浅色" /></td>
  </tr>
  <tr>
    <td align="center"><b>SSH</b></td>
    <td align="center"><b>访问控制（IP 黑白名单）</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/ssh-dark.png" alt="SSH" /></td>
    <td><img src="docs/screenshots/access-light.png" alt="访问控制" /></td>
  </tr>
  <tr>
    <td align="center"><b>内网穿透</b></td>
    <td align="center"><b>设置（主题 / 窗口材质）</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/tunnel-light.png" alt="内网穿透" /></td>
    <td><img src="docs/screenshots/settings-dark.png" alt="设置" /></td>
  </tr>
</table>

<details>
<summary>更多截图（另一配色）</summary>

| | |
| :-: | :-: |
| <img src="docs/screenshots/ssh-light.png" /> | <img src="docs/screenshots/access-dark.png" /> |
| <img src="docs/screenshots/tunnel-dark.png" /> | <img src="docs/screenshots/settings-light.png" /> |

</details>

<details>
<summary>安装 OpenSSH 进度 / 家庭版升级卡片</summary>

| 安装中（切到别的页面再切回，进度仍在） | 家庭版：升级到专业版（CI 用环境变量模拟家庭版渲染） |
| :-: | :-: |
| <img src="docs/screenshots/ssh-installing.png" /> | <img src="docs/screenshots/home-remote-light.png" /> |

</details>

<details>
<summary>English UI 截图</summary>

| | |
| :-: | :-: |
| <img src="docs/screenshots/en/remote-dark.png" /> | <img src="docs/screenshots/en/access-light.png" /> |
| <img src="docs/screenshots/en/tunnel-light.png" /> | <img src="docs/screenshots/en/settings-dark.png" /> |

</details>

## 📦 下载与安装

前往 [**Releases**](https://github.com/Flowers-hurt/RdpHelper/releases/latest) 下载：

| 文件 | 适合 | 说明 |
| :-- | :-- | :-- |
| `RdpHelper-Setup-x.y.z-x64.exe` | 大多数用户 | 安装版，创建开始菜单 / 桌面快捷方式，卸载时自动清理本程序的防火墙规则 |
| `RdpHelper-x.y.z-portable-x64.zip` | U 盘 / 临时使用 | 绿色版，解压即用 |

两者都是 **自包含**（约 60 MB），无需安装 .NET 运行时，并自带微软官方 Win32-OpenSSH 安装包（`redist\`，约 7 MB，附许可证）用于离线安装 SSH 服务器；内网穿透组件仍按需下载、不打包。程序需要管理员权限（修改注册表、防火墙、系统服务），启动时会弹出 UAC 提示。

### 🧰 系统要求

| 项目 | 要求 |
| :-- | :-- |
| 系统 | Windows 10 1809+ / Windows 11 / Windows Server 2019+（x64） |
| 版本 | 被远程的电脑需要 **专业版 / 企业版 / 教育版 / Server**；家庭版没有远程桌面服务端（SSH 功能可用） |
| 毛玻璃 | 亚克力 / 云母效果需要 Windows 11 22H2（22621）及以上，旧系统自动回退为纯色 |

## 🚀 快速上手

```mermaid
flowchart LR
    A([打开 RdpHelper]) --> B{同一局域网?}
    B -- 是 --> C[打开「远程桌面」开关]
    C --> D[一键复制远程信息]
    D --> E([对方 mstsc 输入地址连接])
    B -- 否 --> F[内网穿透 → 选服务商 → 下载组件]
    F --> G[启动穿透，得到公网地址]
    G --> H[复制完整连接信息]
    H --> E
```

1. **开启**：「远程桌面」页打开开关（可选改端口，例如 `33890`）。
2. **复制**：点「一键复制远程信息」，粘贴发给对方。
3. **连接**：对方 `Win + R` → `mstsc` → 填入地址（非默认端口写成 `IP:端口`）→ 输入这台电脑的 Windows 账户密码。
4. **不在同一网络？** 到「内网穿透」选一个服务商启动，复制的信息里会自动带上公网地址和访问方法。

> [!TIP]
> 只想让公司网段连进来？到「访问控制」白名单里点「加入本机局域网网段」，保存即可。

## 🔬 工作原理

### 整体架构

```mermaid
flowchart TB
    subgraph UI["界面层 · WPF + WPF-UI (Fluent / Acrylic)"]
        P1[远程桌面页]
        P2[SSH 页]
        P3[访问控制页]
        P4[内网穿透页]
        P5[设置页]
    end
    subgraph SVC["服务层"]
        R[RdpService]
        S[SshService]
        F[FirewallService]
        N[NetworkService]
        T["TunnelProvider<br/>Cloudflare / ngrok / bore"]
        I[RemoteInfo]
    end
    subgraph OS["Windows"]
        REG[("注册表<br/>Terminal Server")]
        CFG[(sshd_config)]
        FW[["Windows 防火墙"]]
        TS([TermService])
        SD([sshd])
        JOB[[Job Object]]
    end
    P1 --> R & N & I
    P2 --> S
    P3 --> F
    P4 --> T & I
    R --> REG & TS & F
    S --> CFG & SD & F
    T -->|子进程| JOB
    T -.按需下载.-> NET((官方下载地址))
```

### 远程桌面开关与端口

远程桌面的开关和端口都存放在注册表中，和系统「设置 → 系统 → 远程桌面」修改的是同一处：

| 注册表位置 | 值 | 含义 |
| :-- | :-- | :-- |
| `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server` | `fDenyTSConnections` | `0` 允许远程连接，`1` 拒绝 |
| `…\Terminal Server\WinStations\RDP-Tcp` | `PortNumber` | 监听端口，默认 `3389` |
| `…\Terminal Server\WinStations\RDP-Tcp` | `UserAuthentication` | `1` 要求 NLA |

修改端口的完整流程：

```mermaid
sequenceDiagram
    autonumber
    actor U as 用户
    participant App as RdpHelper
    participant Reg as 注册表
    participant FW as Windows 防火墙
    participant TS as TermService
    U->>App: 输入新端口 33890，点「应用」
    App->>App: 校验范围 1024–65535 / 是否被占用
    App->>Reg: PortNumber = 33890
    App->>FW: 删除旧 RdpHelper-RDP-* 规则<br/>新建 TCP/UDP 33890 放行（带黑白名单）
    App->>TS: Restart-Service（卡住时仅在独占进程下强制重启）
    TS-->>App: 开始监听 33890
    App->>App: 轮询确认端口已监听
    App-->>U: ✅ 已改为 33890
```

### 防火墙与 IP 黑白名单

开启服务后，RdpHelper 会**接管放行规则**：禁用系统自带的「远程桌面」规则组（用资源 ID `@FirewallAPI.dll,-28752` 定位，与系统语言无关）和 `OpenSSH-Server-In-TCP`，改用自己的 `RdpHelper-*` 规则，保证端口与名单始终一致。

```mermaid
flowchart TD
    A[入站连接 → 远程桌面 / SSH 端口] --> B{来源 IP 命中黑名单?}
    B -- 是 --> X[❌ Block 规则拦截]
    B -- 否 --> C{白名单为空?}
    C -- 是 --> OK[✅ 放行]
    C -- 否 --> D{来源 IP 在白名单内?}
    D -- 是 --> OK
    D -- 否 --> X2[❌ 无匹配放行规则，默认拒绝]
```

| 规则名 | 作用 |
| :-- | :-- |
| `RdpHelper-RDP-TCP` / `RdpHelper-RDP-UDP` | 远程桌面放行，`RemoteAddress` = 白名单或 `Any` |
| `RdpHelper-RDP-Block-TCP` / `-UDP` | 远程桌面黑名单 |
| `RdpHelper-SSH-TCP` / `RdpHelper-SSH-Block-TCP` | SSH 放行 / 黑名单 |

> [!IMPORTANT]
> 经内网穿透进来的连接，来源地址是本机 `127.0.0.1`（由穿透客户端转发），**不受防火墙名单限制**。公网暴露时请务必使用强密码并保持 NLA 开启。

### SSH

- **安装**：按顺序尝试，进度状态保存在后台服务里，切换页面不会中断或丢失：

```mermaid
flowchart LR
    A[一键安装] --> B{"自带 MSI<br/>redist\OpenSSH-Win64-*.msi"}
    B -- 成功 --> OK[启动 sshd + 防火墙]
    B -- 失败/缺失 --> C{"Windows 可选功能<br/>DISM 显示百分比"}
    C -- 成功 --> OK
    C -- 失败 --> D{"在线下载官方 MSI<br/>显示已下载 MB"}
    D -- 成功 --> OK
    D -- 失败 --> X[提示失败 + 安装日志]
```
- **端口**：删除 `%ProgramData%\ssh\sshd_config` 中所有生效的 `Port` 行，在原 `#Port 22` 注释后写入新值，重启 `sshd`。

## 🌐 内网穿透

| | Cloudflare Tunnel | ngrok | bore |
| :-- | :-: | :-: | :-: |
| 需要注册 | ❌ | ✅ Authtoken | ❌ |
| 费用 | 免费 | 免费档（TCP 需绑卡验证） | 免费 |
| 公网地址 | `xxx.trycloudflare.com` | `x.tcp.ngrok.io:端口` | `bore.pub:端口` |
| 访问方直接用 mstsc | ❌ 需 cloudflared 转发（RdpHelper 内置） | ✅ | ✅ |
| 端口直接暴露在公网 | ❌ 不暴露 | ✅ | ✅ |
| 稳定性 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐（公共服务器） |
| 组件大小（按需下载） | ~52 MB | ~31 MB | ~1 MB |

```mermaid
sequenceDiagram
    participant V as 访问方电脑
    participant E as 穿透服务商边缘节点
    participant C as 穿透客户端<br/>cloudflared / ngrok / bore
    participant H as 本机 TermService / sshd
    Note over C: RdpHelper 启动子进程<br/>并从输出中解析公网地址
    C->>E: 建立出站长连接（无需路由器端口映射）
    V->>E: 连接公网地址
    E->>C: 通过已建立的隧道转发
    C->>H: 127.0.0.1:端口
    H-->>V: RDP / SSH 会话
```

**Cloudflare 的访问方式**：Quick Tunnel 走的是 HTTP(S)，访问方需要在本地运行 `cloudflared access rdp` 把隧道映射成本机端口。如果对方也装了 RdpHelper，在「内网穿透 → 连接别人的电脑」里粘贴地址点「连接」即可，会自动完成转发并打开 `mstsc`。手动方式：

```bash
cloudflared access rdp --hostname xxx.trycloudflare.com --url rdp://localhost:13389
```

然后用 `mstsc` 连接 `localhost:13389`。

**网络不好下载失败？** 从各项目 Release 页手动下载 exe，然后点「导入本地 exe」。组件存放在 `%LOCALAPPDATA%\RdpHelper\tools\`。

## 🧪 自动化测试

每次提交，GitHub Actions 都会在 **真实的 Windows Server 虚拟机** 上运行程序内置的端到端自检（`RdpHelper.exe --selftest report.json`），真实修改系统设置并验证：

| 类别 | 测试项 |
| :-- | :-- |
| 远程桌面 | 开启 → 3389 监听 → 防火墙规则接管 → **RDP 协议握手**（X.224 Connection Confirm）→ NLA 往返 → 改端口 13389 → 握手 → 恢复 3389 → 关闭 |
| 访问控制 | 白名单 / 黑名单写入防火墙并回读 `RemoteAddress`；清空后恢复 `Any`；地址格式校验 |
| SSH | **用自带 MSI 真实安装**（校验 sshd 来自 `Program Files\OpenSSH`）→ 开启 → **SSH banner 握手** → 改端口 22022 → 握手 → 恢复 22；`sshd_config` 改写单元测试 |
| 内网穿透 | 下载 cloudflared / ngrok / bore 并验证版本；**bore 与 Cloudflare 公网端到端 RDP 握手**；退出后无残留进程 |
| 其它 | 局域网 IP 识别、远程信息生成、剪贴板读写、专业版密钥格式校验 |
| 安装进度 | 卸载 OpenSSH → 在 SSH 页点「一键安装」→ 切到别的页面再切回 → 新页面仍显示进度条与阶段 → 装完自动启动、卡片收起 |
| 界面 / 多语言 | 5 个页面 × 浅 / 深色 × 中 / 英 2 倍截图；英文界面可视树无残留中文、无缺失译文；实测设置页切换语言；`tools/check_i18n.py` 静态检查译文覆盖与占位符 |

## 🛠️ 从源码构建

需要 .NET 8 SDK（Windows）：

```bash
git clone https://github.com/Flowers-hurt/RdpHelper.git
```

```bash
dotnet publish RdpHelper/src/RdpHelper/RdpHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

安装包使用 [Inno Setup 6](https://jrsoftware.org/isinfo.php)：`ISCC installer\RdpHelper.iss`。

<details>
<summary>📁 项目结构</summary>

```text
RdpHelper/
├─ src/RdpHelper/
│  ├─ App.xaml(.cs)            # 启动、主题 / 材质、命令行自检入口
│  ├─ MainWindow.xaml(.cs)     # FluentWindow + NavigationView
│  ├─ Views/                   # 远程桌面 / SSH / 访问控制 / 内网穿透 / 设置
│  ├─ Services/
│  │  ├─ RdpService.cs         # 注册表、TermService
│  │  ├─ SshService.cs         # OpenSSH 安装、sshd_config
│  │  ├─ FirewallService.cs    # 防火墙规则 + 黑白名单
│  │  ├─ NetworkService.cs     # 局域网 IP
│  │  ├─ RemoteInfo.cs         # 一键复制文案
│  │  ├─ ChildProcessJob.cs    # Job Object，保证子进程随主程序退出
│  │  └─ Tunnels/              # Cloudflare / ngrok / bore
│  └─ Testing/                 # --selftest / --gui-selftest
├─ installer/RdpHelper.iss     # Inno Setup 脚本
├─ docs/screenshots/           # CI 自动生成的界面截图
└─ .github/workflows/build.yml # 构建 + 真机自检 + 打包 + 发布
```

</details>

## ❓ 常见问题

<details>
<summary><b>家庭版能用吗？</b></summary>

Windows 家庭版没有远程桌面 **服务端**（只能去连别人，不能被连），SSH 功能可正常使用。程序在家庭版上会显示「升级到 Windows 专业版」卡片，只提供正规途径：

- **在微软商店购买升级**（打开商店的专业版升级页）；
- **已有专业版密钥**：输入后调用系统自带 `changepk.exe` 就地升级，保留文件和软件，完成后自动重启；
- **以前激活过专业版**（数字许可证）：用微软公开的专业版通用密钥切换版本，联网后自动激活；没有许可证则显示未激活。

不提供任何绕过激活的方法。

</details>

<details>
<summary><b>对方连接时提示「你的凭据不起作用」？</b></summary>

用户名填这台电脑的 Windows 账户；如果登录 Windows 用的是微软账户，用户名填 **邮箱**，密码填 **微软账户密码**（不是 PIN）。没有设置密码的账户默认禁止远程登录。

</details>

<details>
<summary><b>改端口后连不上了？</b></summary>

地址要写成 `IP:端口`，例如 `192.168.1.10:33890`。如果路由器 / 云服务器安全组还有一层防火墙，也需要放行新端口。

</details>

<details>
<summary><b>卸载后系统会恢复原样吗？</b></summary>

卸载会删除本程序创建的 `RdpHelper-*` 防火墙规则和下载的穿透组件，**不会**改动远程桌面 / SSH 的开关和端口；需要的话先在程序里关闭或恢复默认端口再卸载。

</details>

<details>
<summary><b>会上传我的数据吗？</b></summary>

不会。程序没有任何统计或联网上报，只有你主动点「下载组件」或启动穿透时才会访问对应的官方地址。

</details>

## 🗺️ Roadmap

- [x] 远程桌面开关 / NLA / 端口
- [x] OpenSSH 安装 / 开关 / 端口
- [x] IP 黑白名单
- [x] Cloudflare / ngrok / bore 按需穿透
- [ ] 自建 frp 服务器支持
- [ ] 托盘常驻 + 穿透开机自启
- [ ] 远程登录 / 失败记录查看
- [x] 中英双语界面

## 🤝 参与贡献

欢迎 Issue 和 PR！提交前请确保 CI 自检通过。

## ⚠️ 免责声明

本工具会修改系统远程访问设置，并可能将服务暴露到公网，请仅在你有权管理的设备上使用，并自行承担相应安全风险。穿透服务由第三方提供，其可用性与条款以各服务商为准。

## 📄 许可证

[MIT](LICENSE) © RdpHelper contributors

致谢：[WPF-UI](https://github.com/lepoco/wpfui) · [cloudflared](https://github.com/cloudflare/cloudflared) · [ngrok](https://ngrok.com) · [bore](https://github.com/ekzhang/bore) · [Win32-OpenSSH](https://github.com/PowerShell/Win32-OpenSSH)
