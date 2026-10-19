# Focus Desk

Dock 版本：**1.2.0**。新版源码和文档位于 [`new` 分支](https://github.com/ai-lcs/focus-desktop/tree/new)；默认 `main` 保留旧版。

Focus Desk 是一款 Windows 专注应用：把学习网站、资料、番茄钟和你选择的软件放在同一个专注流程里。它不会查杀名单外的程序，也不接管整台电脑；Dock 只提供已确认的软件入口和返回 Focus Desk 的按钮。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()

## 专注期间使用已选软件

初次配置时，你可以选择专注期间需要使用的软件。Dock 固定在主显示器左侧，包含「返回 Focus Desk」和你确认的入口；它不自动收集正在运行的应用，也不提供分屏或自由排列窗口。没有选择软件时，Focus Desk 按原有方式运行，不显示空 Dock。

支持可解析的 Windows `.lnk` 快捷方式、`.exe` 文件以及带有 AppUserModelID 的打包应用快捷方式。普通程序入口使用确认时的目标程序、参数和工作目录启动；`.lnk` 专属的“以管理员身份运行”和窗口显示方式不会重放。配置后若原快捷方式的目标、参数或工作目录发生变化，Focus Desk 会停止启动该入口，并提示先恢复快捷方式或重新配置。

点击 Dock 入口时，Focus Desk 会尝试切回该应用当前可操作的窗口；找不到可用窗口时再启动已确认的入口。必要的应用弹窗可以随所属窗口一起切换。应用仍按自己的方式处理保存、登录和关闭；Focus Desk 不会为了返回桌面而结束外部应用进程。

在已识别的外部应用窗口中，Alt+F4 和 Alt+Space 可交给该应用处理。Alt+Tab 不是 Dock 的切换方式；按 Dock 上的「返回 Focus Desk」回到学习界面。正常最大化窗口是目标使用方式，F11 等全屏窗口和多显示器场景的 Dock 可见性不作保证。

名单只在首次配置时确认并冻结，不会因桌面新增快捷方式而自动变化。较早的配置若尚未保存过外部应用名单，升级后会出现一次补充选择窗口，可以选择软件或明确跳过。之后如需更换名单，请先备份需要保留的配置和网页登录数据，再按卸载流程清除用户数据并重新安装；清除会删除配置及网站登录状态。

## 下载

从 [GitHub Releases](https://github.com/ai-lcs/focus-desktop/releases/latest) 下载同一版本的安装版、便携版和 SHA-256 清单：

| 文件 | 用途 |
| --- | --- |
| `FocusDesktop-Setup-<版本>.exe` | 安装、升级或卸载 Focus Desk。 |
| `focus-desktop-portable-<版本>.zip` | 解压后运行，不向 Windows 注册安装项。 |
| `v<版本>-sha256.txt` | 核对该版本安装包和便携 ZIP 的 SHA-256。 |

两种构建都包含 .NET 运行时。网页功能需要 Microsoft Edge WebView2 Runtime；如果系统未安装，Focus Desk 会给出安装提示，可从[微软官方页面](https://developer.microsoft.com/microsoft-edge/webview2/)获取。

项目目前未使用代码签名证书，Windows 首次运行时可能显示 SmartScreen 提示。确认文件来自本项目的 Release 后，可在提示中查看发布者信息并自行决定是否运行。

## 首次配置

首次启动会打开配置向导。你可以依次选择学习文件夹、网站、专注语与退出短语、番茄钟参数，以及 Dock 软件名单。Dock 名单可以留空；入口浏览和勾选只会改变当前草稿，只有点击「完成并开始使用」才会和其余配置一起保存。

配置保存后，先按页面提示登录需要的网站，再点击首页的「开始专注」。网页登录状态保存在本机。专注模式运行期间，Focus Desk 保留当前网页会话、番茄钟和 Hard Focus 状态；通过 Dock 切换软件不会重新创建学习界面。

已完成配置的用户如果只想更换 Dock 名单，应先确认卸载时的数据选择。交互卸载默认选中「删除全部用户数据」；选择「否」会只卸载程序并保留配置与网页登录数据。卸载开始前，应用必须成功完成桌面恢复和退出准备；若准备失败，卸载会中止并保留用户数据。无人值守卸载默认保留数据，只有显式传入 `/REMOVEUSERDATA` 才请求清除。卸载程序不会删除学习文件夹；配置向导也不允许把学习文件夹放在应用目录或数据目录内。

## 日常操作

| 操作 | 方式 |
| --- | --- |
| 开始或暂停计时 | 首页按空格，或使用番茄钟卡片。 |
| 切换学习网站 | 点击顶部的网站标签；「+」可打开同一网站的第二个页面。 |
| 查看学习资料 | 打开「学习文件」，选择文件后在应用内查看。 |
| 使用已选软件 | 点击 Dock 对应入口；返回学习界面时点击「返回 Focus Desk」。 |
| 退出专注 | 在 Focus Desk 首页点击「退出」，输入配置时设置的短语。 |

Hard Focus 用于特别容易分心的专注段。开启后不能用普通按钮关闭或重置当前专注段；Windows 安全界面仍由系统控制。

## 配置损坏与恢复

如果配置 JSON 或冻结的软件名单无法通过校验，Focus Desk 会显示错误并停止启动，不会把原文件改成默认配置，也不会进入锁定。先恢复配置备份或手动修复错误字段，再重新启动；不要为了处理配置错误直接卸载，因为卸载时默认选择会清除配置和网页登录数据。

Focus Desk 会在异常退出后尝试恢复任务栏和 Dock。安装版也会创建「Focus Desk 恢复」快捷方式；便携版可在应用目录运行：

```powershell
.\focus-desktop.exe --restore
```

若软件入口指向的程序被移动或卸载，请恢复该程序或重新配置名单。仍存在的原 `.lnk` 会在启动时重新解析并与已冻结的目标、参数和工作目录核对；它被改指向时会拒绝启动。若原 `.lnk` 已删除，Focus Desk 仍按冻结的目标身份启动，不会借用其他快捷方式替换入口。

## 隐私与实现

Focus Desk 是本机 Windows 应用，不提供账号、云同步或遥测。配置、网站登录数据和日志保存在本机：安装版使用 `%LOCALAPPDATA%\focus-desktop`；便携版把 `focus-desktop-data` 放在应用目录旁。

Dock 按打包应用的 AppUserModelID 或 Win32 可执行文件路径识别窗口，不按标题或进程显示名推断归属。Focus Desk 使用 Windows Shell 启动已确认的软件，不嵌入第三方窗口、不控制名单外程序，也不限制外部应用访问的网站。便携 ZIP 和安装目录均附带 [第三方组件与许可证说明](THIRD_PARTY_NOTICES.md)。

## 开发与验证

项目使用 C#、WPF、.NET 10 和 WebView2。安装包与便携 ZIP 通过同一项目构建；构建脚本把临时发布文件放入隔离目录，只将该版本的 Setup、ZIP 和校验清单放进新建的候选目录。默认目录已存在时会停止，不会覆盖旧产物。

```powershell
dotnet run --project .\src\focus-desktop -- --dev
dotnet run --project .\src\focus-desktop -- --dockselftest
powershell -ExecutionPolicy Bypass -File .\installer\build-release.ps1
```

`--dockselftest` 运行无 UI 的入口身份、配置校验、启动请求合并、异步失败观察和恢复边界检查；它使用临时文件，不创建窗口或注册 AppBar。桌面和安装验收工具见 [`tests/README.md`](tests/README.md)。执行任何桌面、安装、卸载或真实数据隔离测试前，都应先读该目录的安全说明。

## License

[MIT](LICENSE)
