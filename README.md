# Focus Desk

把学习网站、资料、计时器和指定软件放进一个 Windows 专注桌面。

**主要版本：Focus Desk Dock 1.2.4，源码主线为 [`master`](https://github.com/ai-lcs/focus-desktop/tree/master)。**

[下载主版本](https://github.com/ai-lcs/focus-desktop/releases/latest) · [发布说明](docs/releases/v1.2.4.md) · [本机验收](docs/acceptance/dock-1.2.4-taskbar-reveal-2026-11-07.md)

## 下载与版本

| 版本 | 分支 | 下载 |
| --- | --- | --- |
| **新版主版本：1.2.4**，含软件 Dock | [`master`](https://github.com/ai-lcs/focus-desktop/tree/master) | [Release / Latest](https://github.com/ai-lcs/focus-desktop/releases/tag/v1.2.4) |
| 旧版保留版本：1.0.8，无软件 Dock | [`main`](https://github.com/ai-lcs/focus-desktop/tree/main) | [旧版 Release](https://github.com/ai-lcs/focus-desktop/releases/tag/v1.0.8) |

`master` 是默认分支和后续开发主线，原新版分支 `new` 已改名为 `master`；`main` 保留成熟旧版。Releases 只保留两条线各自的最新版，历史源码标签继续保留。

新版附件适用于 Windows x64：

| 文件 | 用途 |
| --- | --- |
| [FocusDesk-Dock-Setup-1.2.4.exe](https://github.com/ai-lcs/focus-desktop/releases/download/v1.2.4/FocusDesk-Dock-Setup-1.2.4.exe) | 安装版，创建桌面、开始菜单和恢复入口。 |
| [focus-desk-dock-portable-1.2.4.zip](https://github.com/ai-lcs/focus-desktop/releases/download/v1.2.4/focus-desk-dock-portable-1.2.4.zip) | 便携版，解压到独立目录后运行 `focus-desktop.exe`。 |
| [dock-v1.2.4-sha256.txt](https://github.com/ai-lcs/focus-desktop/releases/download/v1.2.4/dock-v1.2.4-sha256.txt) | 两个发布包的 SHA-256 校验值。 |

程序包含 .NET 运行时；网页功能需要 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。本项目未使用代码签名证书。

## 主要功能

- **学习网站**：网页标签、网站白名单、登录状态保留；同一网站可打开多个页面。
- **学习资料**：首次设置时自选文件夹，可放在 D 盘等位置，在应用内浏览资料和支持的文件。
- **计时与专注**：自由计时、可配置番茄钟、专注语和 Hard Focus。
- **指定软件 Dock**：从左侧固定入口使用确认过的软件，顶部网页、学习文件、时间、音量和退出栏保持可用。
- **完整可用区域**：普通外部主窗口铺满两个固定栏之外的区域，必要弹窗保留自身尺寸。
- **系统状态恢复**：专注时隐藏 Windows 任务栏，防护鼠标触底唤出；退出后恢复任务栏与工作区。

1.2.4 修复了任务栏触底上浮和系统通知引起的固定栏短暂位移，同时包含底部空隙、启动/退出布局跳变及窗口位置恢复的修复。

## 第一次使用

1. 在向导中选择**学习文件夹**。学习目录不要放进应用目录或应用数据目录。
2. 选择学习网站，设置退出短语、专注语及番茄钟参数。
3. 确认需要的软件。支持可解析的 `.lnk`、`.exe` 和带 AppUserModelID 的打包应用入口，也可以明确跳过 Dock。
4. 完成设置后先登录需要的网站，再在首页点击**开始专注**。

软件名单在首次确认后冻结，不会自动添加正在运行的软件或新的桌面快捷方式。同一个 exe 或 AppUserModelID 只保留一个入口，不承诺按不同启动参数切换到特定项目或文档。

## 日常操作

| 操作 | 方式 |
| --- | --- |
| 切换网站或资料 | 点击顶部网页标签或「学习文件」。 |
| 使用指定软件 | 点击左侧 Dock 的应用入口。 |
| 返回学习界面 | 点击「返回 Focus Desk」，或顶部网页、学习文件入口。 |
| 开始或暂停自由计时 | 在首页点击计时按钮，或按空格。 |
| 退出专注 | 点击顶部「退出」，输入配置时设置的退出短语。 |

切换软件时保留网页、输入内容和专注计时。程序优先使用已有应用窗口，找不到可用窗口时才启动已确认的入口；返回时不会结束外部程序。正常退出时会尝试还原本会话调整过的窗口位置。

## 数据与旧版隔离

| 类型 | 数据位置 |
| --- | --- |
| 新版安装版 | `%LOCALAPPDATA%\focus-desktop-dock` |
| 便携版 | 程序旁的 `focus-desktop-data`，由 `portable.flag` 启用 |
| 旧版安装版 | `%LOCALAPPDATA%\focus-desktop`，新版不自动读取或迁移 |

新版使用独立安装标识和默认目录 `FocusDeskDock`，不要安装或解压到旧版目录。

更新便携版时，先正常退出程序及其 WebView2 进程，备份新版数据，再解压到独立目录。需要保留设置和登录状态时，只从原有的**新版**数据迁移；不要复制 `session_state.json` 等恢复状态。

交互卸载默认保留配置和网页登录数据；选择清理应用数据时仍保留学习文件夹。配置损坏时程序会提示并停止进入专注，不会用默认配置覆盖原文件；请恢复备份或修复配置，无需删除网页登录数据。

Focus Desk 不提供账号、云同步或遥测。配置、网页登录数据和日志保存在本机，发布包不含个人配置或登录信息。第三方组件见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 异常恢复

异常退出后，看门狗会尝试恢复任务栏与工作区。安装版可使用开始菜单中的 **Focus Desk Dock 恢复**；便携版可在对应程序目录运行：

```powershell
.\focus-desktop.exe --restore
```

恢复助手会核对会话所有权；未确认恢复时保留标记，不会自动强杀 Explorer。两版数据虽独立，仍共享同一桌面：不要同时运行两版专注，也不要在新版专注期间启动旧版恢复或卸载助手。

## 适用范围与验证

Focus Desk 面向单屏日常专注。Dock 名单是入口管理，**不是系统安全沙箱**：它不限制外部软件内部的网站，也不阻止这些软件通过链接、终端等方式打开其他程序。

1.2.4 在本机 2560×1600、200% 缩放下完成了底边停留、移动、点击、任务栏主动显示回归，以及正常退出、启动中断、运行中断恢复检查。切换、配置和恢复逻辑的详细方法见[验收记录](docs/acceptance/dock-1.2.4-taskbar-reveal-2026-11-07.md)。

多屏、独占全屏、所有第三方软件及弹窗、长时间压力和隔离环境中的完整安装/升级/卸载矩阵尚未全部验证。构建安装包不等于这些矩阵已通过。

## 开发

使用 C#、WPF、.NET 10 和 WebView2，安装包由 Inno Setup 6 构建。

```powershell
git clone https://github.com/ai-lcs/focus-desktop.git
cd focus-desktop
git switch master
dotnet run --project .\src\focus-desktop -- --dev
dotnet run --project .\src\focus-desktop -- --dockselftest
powershell -ExecutionPolicy Bypass -File .\installer\build-release.ps1
```

构建脚本使用新输出目录生成安装包、便携包和校验清单，上传时使用上述 Dock 专属附件名。桌面及安装测试会接管系统状态，执行前请阅读 [tests/README.md](tests/README.md)，不要直接运行标明风险的历史脚本。

## License

[MIT](LICENSE)
