# Focus Desk

本来只是想打开电脑学点东西，结果一会儿刷网页，一会儿点开 QQ、微信，半小时就过去了。Focus Desk 是我给自己做的一个专注桌面：把要用的网站、资料和软件留在眼前，让那些顺手分心的动作少一点。

如果你也需要一个这样的学习环境，可以试试。

[下载最新版](https://github.com/ai-lcs/focus-desktop/releases/latest) · [2.0.0 更新说明](docs/releases/v2.0.0.md)

## 用起来是什么样

打开后，你会看到学习文件、网页标签和计时器。开始专注后，Windows 任务栏会隐藏，常用的切换快捷键也会受到限制。想退出时，需要输入自己设定的退出短语。

看课、读资料时，内置网页和文件页通常就够了。编程时还要用 Codex、VS Code？首次设置时把它们选进来，左侧就会出现软件栏。点击软件切过去，点击“返回 Focus Desk”回到首页；顶部的网站、文件和退出按钮一直保留。侧栏会标出当前使用的软件。

**不需要外部软件的话，直接跳过软件选择。** 这样没有左侧软件栏，仍是原来的学习桌面，时间放在左上角，右上角显示电量。台式机没有电池时，会显示“交流供电”。两种用法都在同一个安装包里。

还有这些日常会用到的功能：

- 网站白名单、自定义网站，同一网站可以开多个标签；登录状态保存在本机。
- 自选学习文件夹，支持查看 PDF、图片和 TXT，资料可以放在 D 盘。
- 自由计时、番茄钟、首页专注语，以及可选的 Hard Focus。开启 Hard Focus 后，当前专注段内不能通过普通退出按钮离开，系统紧急通道仍保留。
- 切换网页和软件时保留页面、输入内容及计时。返回学习桌面不会结束外部程序。

## 下载与首次设置

目前提供 Windows x64 版本，最新版是 **2.0.0**。

| 下载 | 怎么选 |
| --- | --- |
| [安装版](https://github.com/ai-lcs/focus-desktop/releases/download/v2.0.0/FocusDesk-Setup-2.0.0.exe) | 大多数人选这个，安装后有桌面入口和恢复入口。 |
| [便携版](https://github.com/ai-lcs/focus-desktop/releases/download/v2.0.0/focus-desk-portable-2.0.0.zip) | 解压到独立文件夹，运行 `focus-desktop.exe`。 |
| [SHA-256 校验清单](https://github.com/ai-lcs/focus-desktop/releases/download/v2.0.0/focus-desk-2.0.0-sha256.txt) | 用于核对下载的文件。 |

程序自带 .NET 运行时，网页部分需要 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。安装包没有代码签名，Windows 首次运行可能弹出 SmartScreen 提示；请先确认下载来自本仓库的 Release。

首次打开，按向导做四件事：

1. 选学习文件夹。选你放资料的位置，不要选程序或应用数据目录。
2. 选学习网站，也可以添加自己的网址。
3. 设置退出短语、专注语和番茄钟。
4. 选择需要的软件，或者点击“跳过软件，仅使用学习桌面”。

配置完成后先登录需要的网站，再点首页的“开始专注”。软件可以从桌面快捷方式中选择，也可以浏览其他位置；支持可解析的 `.lnk`、`.exe` 和打包应用入口。

## 有几件事先说清楚

配置确认后，日常界面不提供修改入口。这是有意的：专注时，我不想再多一个可以反复折腾的设置页。**要重新配置，卸载后重新安装。** 这会清除配置和网页登录状态，学习文件夹中的资料会保留。卸载会直接清除配置、网页登录状态和日志，不再询问是否保留数据。

软件栏提供的是你选定软件的入口。外部软件仍可能通过链接、终端等方式打开其他程序或网页；Focus Desk 无法把整台电脑变成严格的软件白名单环境。同一 exe 或 AppUserModelID 只保留一个入口，也不保证按不同启动参数切到特定项目或文档。

目前主要按单屏、普通窗口和最大化窗口使用。多屏、F11 或独占全屏、各家软件的保存和登录弹窗，还没有全部测完。如果你依赖某个软件，建议先用可丢弃内容试一次切换和退出，再开始正式工作。

## 更新、数据和恢复

**安装版更新：** 正常退出 Focus Desk 后，运行新版安装包。安装版沿用 1.2.x 的数据路径，数据在 `%LOCALAPPDATA%\focus-desktop-dock`，升级前可以备份这个目录。

**便携版更新：** 退出程序及其网页进程，备份原文件夹，再把新版解压到独立目录。需要保留数据时，只迁移同一产品线的 `focus-desktop-data`；不要复制 `session_state.json` 等恢复标记。

1.0.x 旧版的安装身份和数据目录独立，当前版本不会自动迁移 `%LOCALAPPDATA%\focus-desktop` 中的数据。不要覆盖旧版目录，也不要同时运行两版专注或恢复助手。历史版本仍可从 Git 标签和 Releases 找到，后续开发集中在 `master`。

如果异常退出后任务栏没有回来，先关闭 Focus Desk，运行开始菜单中的“Focus Desk 恢复”。便携版在对应目录运行：

```powershell
.\focus-desktop.exe --restore
```

恢复助手会检查会话归属；无法确认恢复结果时会保留标记并提示，不会强制结束 Explorer。配置文件损坏时也会停止进入专注，请恢复备份或修复配置，无需连同网页登录数据一起删除。

Focus Desk 没有账号、云同步和遥测。配置、网页登录数据和日志都保存在本机，发布包不包含个人配置或学习资料。

## 开发与验证

项目使用 C#、.NET 10、WPF、WebView2 和 Inno Setup 6，代码位于 `master`。

```powershell
git clone https://github.com/ai-lcs/focus-desktop.git
cd focus-desktop
dotnet run --project .\src\focus-desktop -- --dev
dotnet run --project .\src\focus-desktop -- --dockselftest
powershell -ExecutionPolicy Bypass -File .\installer\build-release.ps1
```

测试方法与限制见 [tests/README.md](tests/README.md)，本版复查见 [2.0.0 验证记录](docs/acceptance/v2.0.0-2026-11-09.md)。桌面和安装测试会影响系统状态，请先读说明；历史安装脚本不适合直接在日常使用的电脑上运行。

[MIT License](LICENSE) · [第三方组件说明](THIRD_PARTY_NOTICES.md)
