# Focus Desk

Dock 修复候选版本：**1.2.1-rc.1**（程序内部版本 1.2.1），以预发布形式提供。新版源码和文档位于 [`new` 分支](https://github.com/ai-lcs/focus-desktop/tree/new)；默认 `main` 保留旧版。

| 产品线 | 源码分支 | 下载 |
| --- | --- | --- |
| 旧版，无外部软件 Dock | [`main`](https://github.com/ai-lcs/focus-desktop/tree/main) | [v1.0.8](https://github.com/ai-lcs/focus-desktop/releases/tag/v1.0.8) |
| Dock 新版，开发候选 | [`new`](https://github.com/ai-lcs/focus-desktop/tree/new) | [dock-v1.2.1-rc.1 预发布](https://github.com/ai-lcs/focus-desktop/releases/tag/dock-v1.2.1-rc.1) |

两条产品线独立维护。Dock 候选的验证范围与尚未完成的验收见[发布说明](docs/releases/dock-v1.2.1-rc.1.md)。

Focus Desk 是一款 Windows 专注应用：把学习网站、资料、番茄钟和你选择的软件放在同一个专注流程里。它不会查杀名单外的程序，也不接管整台电脑；Dock 只提供已确认的软件入口和返回 Focus Desk 的按钮。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()

## 专注期间使用已选软件

新版安装名为 Focus Desk Dock，使用独立安装标识、默认目录 FocusDeskDock 和数据目录，不自动读取或迁移旧版配置；便携版继续使用各自目录内的数据。不要将新版解压覆盖旧目录。

初次配置时，你可以选择专注期间需要使用的软件。Dock 固定在主显示器左侧，包含「返回 Focus Desk」和你确认的入口；它不自动收集正在运行的应用，也不提供分屏或自由排列窗口。没有选择软件时，Focus Desk 按原有方式运行，不显示空 Dock。

正式专注期间隐藏 Windows 任务栏，保留左侧应用 Dock。Focus Desk 与 Dock 按显示器完整高度布局，覆盖任务栏隐藏后仍被系统保留的底部区域；退出后恢复任务栏。Dock 只提供已确认的应用入口，并非系统级运行限制，也不拦截外部软件通过终端、链接等方式启动其他程序。

支持可解析的 Windows `.lnk` 快捷方式、`.exe` 文件以及带有 AppUserModelID 的打包应用快捷方式。普通程序入口使用确认时的目标程序、参数和工作目录启动；`.lnk` 专属的“以管理员身份运行”和窗口显示方式不会重放。配置后若原快捷方式的目标、参数或工作目录发生变化，Focus Desk 会停止启动该入口，并提示先恢复快捷方式或重新配置。

点击 Dock 入口时，Focus Desk 会尝试切回该应用当前可操作的窗口；找不到可用窗口时再启动已确认的入口。必要的应用弹窗可以随所属窗口一起切换。应用仍按自己的方式处理保存、登录和关闭；Focus Desk 不会为了返回桌面而结束外部应用进程。

每个 exe 路径或 AppUserModelID 只能选择一个应用入口。已有窗口优先复用，启动参数和工作目录仅在找不到可用窗口时使用；不保证切到指定项目、文档或浏览器配置。不同名称、不同参数的快捷方式如果指向同一应用，也不能同时选中。同一应用的多个窗口仍可使用最近窗口与必要弹窗返回。

取消或超时后的旧 Dock 请求不会再次主动激活窗口。第三方应用延迟创建窗口时，仍可能自行弹到前台；可以通过 Dock 返回，专注计时和网页会话不会因此重建。

在已识别的外部应用窗口中，Alt+F4 和 Alt+Space 可交给该应用处理。Alt+Tab 不是 Dock 的切换方式；按 Dock 上的「返回 Focus Desk」或 Ctrl+Alt+F10 回到学习界面；快捷键也适用于外部软件全屏遮住 Dock 的情况，仅返回，不结束专注。若快捷键已被其他程序占用，本次会取消进入 Dock 专注。正常最大化窗口是目标使用方式，F11 等全屏窗口和多显示器场景的 Dock 可见性不作保证。

名单只在首次配置时确认并冻结，不会因桌面新增快捷方式而自动变化。较早的配置若尚未保存过外部应用名单，升级后会出现一次补充选择窗口，可以选择软件或明确跳过。日常界面不提供名单编辑器；重新配置前请先备份。旧配置包含同一应用的多个入口时，新版会停止进入专注并说明兼容问题，不会自动删除、排序或改写入口。退出程序后，可备份并仅调整配置的 `externalApps`，每个 exe/AUMID 保留一个完整入口；无需清除其他设置或 `WebViewProfile`。

## 下载

从 [Dock 新版专用预发布](https://github.com/ai-lcs/focus-desktop/releases/tag/dock-v1.2.1-rc.1) 下载同一版本的安装版、便携版和 SHA-256 清单，不使用通用 Latest 链接选择产品线：

| 文件 | 用途 |
| --- | --- |
| `FocusDesk-Dock-Setup-1.2.1-rc.1.exe` | 安装、升级或卸载 Dock 新版。 |
| `focus-desk-dock-portable-1.2.1-rc.1.zip` | 解压到独立目录运行，不向 Windows 注册安装项。 |
| `dock-v1.2.1-rc.1-sha256.txt` | 核对本候选安装包和便携 ZIP 的 SHA-256。 |

两种构建都包含 .NET 运行时。网页功能需要 Microsoft Edge WebView2 Runtime；如果系统未安装，Focus Desk 会给出安装提示，可从[微软官方页面](https://developer.microsoft.com/microsoft-edge/webview2/)获取。

项目目前未使用代码签名证书，Windows 首次运行时可能显示 SmartScreen 提示。确认文件来自本项目的 Release 后，可在提示中查看发布者信息并自行决定是否运行。

## 首次配置

首次启动会打开配置向导。你可以依次选择学习文件夹、网站、专注语与退出短语、番茄钟参数，以及 Dock 软件名单。Dock 名单可以留空；入口浏览和勾选只会改变当前草稿，只有点击「完成并开始使用」才会和其余配置一起保存。

配置保存后，先按页面提示登录需要的网站，再点击首页的「开始专注」。网页登录状态保存在本机。专注模式运行期间，Focus Desk 保留当前网页会话、番茄钟和 Hard Focus 状态；通过 Dock 切换软件不会重新创建学习界面。

交互卸载默认保留配置与网页登录数据。只有明确选择「是」或传入 `/REMOVEUSERDATA` 才请求清除；助手必须确认交互账户、数据目录归属、profile 释放和桌面恢复，在持续持有协调权时完成删除。无法证明安全时保留数据并中止卸载。卸载程序不会在助手退出后再次删除数据，也不会删除学习文件夹；配置向导不允许把学习文件夹放在应用目录或数据目录内。

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

如果配置 JSON 或冻结的软件名单无法通过校验，Focus Desk 会显示错误并停止启动，不会把原文件改成默认配置，也不会进入锁定。先恢复配置备份或手动修复错误字段，再重新启动；不要为了处理配置错误清除网页登录数据。

Focus Desk 会在异常退出后尝试恢复任务栏和 Dock。安装版也会创建「Focus Desk 恢复」快捷方式；便携版可在应用目录运行：

```powershell
.\focus-desktop.exe --restore
```

新版恢复助手会先取得当前桌面的会话协调权并核对恢复记录；其他路径的 Focus Desk 正在使用桌面时会拒绝操作。恢复失败会保留标记，不会自动强制结束 Explorer。可先正常退出其他 Focus Desk 实例，检查任务栏与显示器布局后重试；仍未恢复时保存其他应用的工作，再由用户选择注销并重新登录 Windows。

该保护适用于新版主程序与新版助手。已发布的旧版恢复程序、卸载器或看门狗不会随新版源码改变；新版专注期间请勿运行旧版恢复或卸载操作。旧版、新版数据独立并不能隔离同一桌面的任务栏。

若软件入口指向的程序被移动或卸载，请恢复该程序或重新配置名单。仍存在的原 `.lnk` 会在启动时重新解析并与已冻结的目标、参数和工作目录核对；它被改指向时会拒绝启动。若原 `.lnk` 已删除，Focus Desk 仍按冻结的目标身份启动，不会借用其他快捷方式替换入口。

## 隐私与实现

Focus Desk 是本机 Windows 应用，不提供账号、云同步或遥测。配置、网站登录数据和日志保存在本机：安装版使用 `%LOCALAPPDATA%\focus-desktop-dock`；便携版把 `focus-desktop-data` 放在应用目录旁。

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
