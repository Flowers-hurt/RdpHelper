# 贡献指南 / Contributing

**简体中文** | [English](#english)

感谢你愿意为 RdpHelper 出一份力！报告问题、提出建议、改进文档、翻译、写代码，都是贡献。

## 🐞 报告问题

1. 先在 [Issues](https://github.com/Flowers-hurt/RdpHelper/issues) 搜索是否已有相同问题。
2. 在 RdpHelper 里打开 **关于 → 复制诊断信息**，会复制版本、系统、远程桌面 / SSH 状态（不含用户名和 IP）。
3. 用 [Bug 报告模板](https://github.com/Flowers-hurt/RdpHelper/issues/new?template=bug_report.yml) 提交，附上复现步骤和截图；穿透问题请附「运行日志」，SSH 安装问题请附「安装日志」。

安全漏洞请走 [私下报告](SECURITY.md)，不要公开提 Issue。

## 🛠️ 开发环境

| 需要 | 说明 |
| :-- | :-- |
| Windows 10 1809+ / 11 | WPF 只能在 Windows 上运行；修改远程桌面等功能需要管理员权限 |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | `dotnet --version` ≥ 8.0 |
| IDE | Visual Studio 2022 / Rider / VS Code + C# Dev Kit |

```bash
git clone https://github.com/Flowers-hurt/RdpHelper.git
cd RdpHelper
dotnet build src/RdpHelper/RdpHelper.csproj
```

> 程序清单要求管理员权限，在 IDE 中调试请以管理员身份启动 IDE。

## 🔀 提交流程

1. Fork 仓库，从 `main` 新建分支：`feat/xxx`、`fix/xxx`、`docs/xxx`。
2. 修改代码，尽量保持一个 PR 只做一件事。
3. 本地检查：

   ```bash
   python tools/check_i18n.py                     # 译文覆盖、占位符一致、无残留中文
   RdpHelper.exe --selftest report.json           # 引擎自检（会真实修改本机设置，请在虚拟机里跑）
   RdpHelper.exe --gui-selftest shots --lang en   # 界面自检 + 2 倍截图
   ```

4. 提交 Pull Request，按模板勾选自检项。CI 会在真实 Windows 虚拟机上跑完整自检并产出截图，全绿后合并。

## 🌐 本地化

- 源码里直接写中文原文作为 key：C# 用 `L.T("原文")` / `L.F("端口 {0}", port)`，XAML 用 `Text="{l:T '原文'}"`。
- 英文译文写在 [`src/RdpHelper/Localization/en.json`](src/RdpHelper/Localization/en.json)，`{0}` 等占位符必须与原文一致。
- XAML 里的反斜杠要写成 `\\`（标记扩展转义）。
- 新增语言：欢迎先开 Issue 讨论。

## 📐 代码约定

- 面向用户的文案一律本地化，不要在界面上拼接半句中文。
- 修改系统设置的操作放在 `Services/`，并在 `Testing/SelfTest.cs` 里加对应的端到端自检。
- 长时间任务（安装、下载）的状态放在静态服务里，页面只负责显示，切换页面不能丢进度。
- 不引入需要额外安装的运行时；新增第三方组件请在「关于 → 第三方开源组件」和 README 致谢里登记许可证。

## 📜 许可

提交贡献即表示你同意以 [MIT 许可证](LICENSE) 发布你的代码。

---

<a id="english"></a>

## English

Thanks for helping out! Bug reports, ideas, docs, translations and code are all welcome.

- **Bugs**: search existing issues first, then use the [bug report form](https://github.com/Flowers-hurt/RdpHelper/issues/new?template=bug_report.yml). *About → Copy diagnostics* in the app copies your environment details (no user name or IP). Report security issues [privately](SECURITY.md).
- **Build**: Windows + .NET 8 SDK, then `dotnet build src/RdpHelper/RdpHelper.csproj`. Run your IDE as administrator to debug.
- **Workflow**: fork → branch from `main` → PR. Run `python tools/check_i18n.py` locally; CI runs the full engine and GUI self-tests on a real Windows VM.
- **Localization**: source strings are the Chinese text used as keys (`L.T("…")`, `{l:T '…'}`); English lives in `src/RdpHelper/Localization/en.json` with matching `{n}` placeholders.
- **License**: contributions are released under the [MIT License](LICENSE).
