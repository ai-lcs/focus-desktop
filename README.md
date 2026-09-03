# Focus Desk

一个给自己用的 Windows 专注桌面。

如果你打开电脑本来想学习，结果一转眼就在网页之间来回乱逛，Focus Desk 想做的事情很简单：把真正需要的资料和网站放在眼前，把那些下意识的“顺手打开”变得麻烦一点。

它不是家长控制软件，也不是把电脑锁死的工具。你仍然保留系统级的紧急出口，只是平时不再那么容易被一时冲动带跑。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()

## 它能做什么

- 用一个全屏的专注桌面承载学习资料、计时器和网页；
- 只显示你选择的网站，也可以自己添加网址；
- 在应用里直接浏览 PDF、图片和 TXT 等学习资料；
- 提供番茄钟，并支持可选的 Hard Focus 模式；
- 退出时要求输入你自己设置的短语，减少无意识退出；
- 程序异常退出时尽量自动恢复任务栏和桌面；
- 保留 Ctrl+Alt+Del 和任务管理器，遇到问题始终有系统级出口。

## 下载

前往 [GitHub Releases](https://github.com/ai-lcs/focus-desktop/releases/latest) 下载最新版（当前为 `v1.0.7`）。

| 版本 | 适合谁 |
| --- | --- |
| `FocusDesktop-Setup-1.0.7.exe` | 大多数用户。正常安装、升级和卸载。 |
| `focus-desktop-portable-1.0.7.zip` | 不想安装的用户。解压后直接运行。 |

两种版本功能一致，应用本身是自包含发布，不需要另外安装 .NET。WebView2 是 Windows 上的网页内核；如果电脑没有它，应用会给出安装提示，可从[微软官方页面](https://developer.microsoft.com/microsoft-edge/webview2/)安装。

### Windows SmartScreen 提示

项目目前没有购买代码签名证书，所以第一次运行时，Windows 可能显示“已保护你的电脑”。这是 SmartScreen 对未签名程序的常见提示：点击“更多信息”，再点击“仍要运行”即可。

你也可以在下载后自行校验 SHA-256。Release 页面同时提供 `v1.0.7-sha256.txt`，PowerShell 命令示例：

```powershell
Get-FileHash .\FocusDesktop-Setup-1.0.7.exe -Algorithm SHA256
```

## 第一次使用

第一次启动会出现一个很短的配置向导：

1. 选择学习文件夹，例如放课程资料、论文和笔记的文件夹；
2. 选择需要的网站，也可以添加自己的网址；
3. 写下首页想展示的话、退出短语，并设置番茄钟参数。

每一步都可以先预览。确认配置后，去需要的网站登录一次，再点击“开始专注”。登录状态保存在本机的浏览器数据中，不会上传到 Focus Desk 的服务器。

配置完成后，日常界面不会提供随手修改配置的入口。这是有意的：专注时少一个可以反复折腾的设置，就少一个分心的机会。需要重新配置时，可以卸载并在卸载提示中选择删除配置和登录数据，再重新安装；学习文件夹不会被卸载程序删除。

## 日常操作

| 想做什么 | 怎么做 |
| --- | --- |
| 开始或暂停计时 | 在首页按空格，或使用番茄钟卡片 |
| 打开课程网站或 AI 工具 | 点击顶部对应的网站标签 |
| 打开同一个网站的第二个页面 | 点击标签栏的“+” |
| 查看学习资料 | 打开“学习文件”，点击文件即可在应用内查看 |
| 调整音量 | 使用顶部音量滑块 |
| 退出专注 | 点击右上角“退出”，输入配置时设置的短语 |

Hard Focus 适合“今天特别容易分心”的时候。开启后，当前专注段内不能用普通按钮关闭或重置；系统级紧急通道仍然保留。

## 如果遇到问题

Focus Desk 会尽量在退出、崩溃、被任务管理器结束或断电后恢复任务栏。安装版还会创建“Focus Desk 恢复”快捷方式；便携版可以运行：

```powershell
focus-desktop.exe --restore
```

如果只是忘记了退出短语，可以通过 Ctrl+Alt+Del 打开任务管理器，结束 `focus-desktop.exe`。学习文件夹不会因为这个操作被删除。

常见情况：

- **网页打不开**：确认网址已加入白名单，并检查 WebView2 Runtime 是否正常安装；
- **下载后无法运行**：先按上面的 SmartScreen 说明放行一次；
- **想换电脑**：重新安装并运行向导即可。登录状态默认不迁移；便携版则可以连同旁边的 `focus-desktop-data` 文件夹一起移动。

## 给想了解细节的人

Focus Desk 是 Windows 单机应用，使用 C#、.NET 10、WPF 和 WebView2 构建。它没有账号、服务器、云同步和遥测，配置及网页登录数据都保存在本机。

代码大致分为三部分：

- `src/focus-desktop/`：应用本体、配置向导、首页和网页标签；
- `installer/`：安装包及便携版构建脚本；
- `tests/`：锁定、首次配置、安装和白名单等自动验收脚本。

本地开发：

```powershell
dotnet run --project src/focus-desktop -- --dev
```

构建安装包和便携版：

```powershell
powershell -ExecutionPolicy Bypass -File installer/build-release.ps1
```

第三方组件和许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## License

[MIT](LICENSE)
