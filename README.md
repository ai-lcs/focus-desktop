# Focus Desk

> Windows 专注学习环境：全屏白名单浏览器 + 学习资料 + 番茄钟。把「想刷别的网站」变成一件麻烦事。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()

---

## 它解决什么问题

学习时想「就查一个东西」→ 顺手打开了微博/知乎/B 站首页 → 40 分钟没了。

Focus Desk 是一个**行为摩擦**工具，不是家长管控：

- 启动即全屏盖住桌面、隐藏任务栏，拦截 Win / Alt+Tab / Alt+F4 等常见逃离路径；
- 只有你勾选的网站能打开（B 站、ChatGPT、Gemini、DeepSeek，或你自己添加的网站）；
- 退出需要完整输入你预设的一句话——不是锁死，是让「无意识溜走」变得很麻烦；
- **保留** Ctrl+Alt+Del 和任务管理器（系统级救生通道，故意不拦）。

## 下载安装

| 方式 | 文件 | 适合 |
|---|---|---|
| **安装版（推荐）** | `FocusDesktop-Setup-1.0.0.exe`（约 48 MB） | 大多数人：正规安装/升级/卸载，开始菜单+桌面快捷方式 |
| 便携版 | `focus-desktop-portable-1.0.0.zip`（约 62 MB） | 免安装：解压到任意文件夹，双击 `focus-desktop.exe` 即用 |

- 两种版本功能完全一致；免 .NET 安装（自包含）；
- 安装版装到用户目录（不需要管理员权限），数据存放在 `C:\Users\<你>\AppData\Local\focus-desktop`；
- 便携版所有数据在 exe 旁的 `focus-desktop-data\` 文件夹，整个文件夹可随 U 盘带走；
- 升级：直接装新版本覆盖，配置和网站登录态全部保留；
- 卸载：控制面板卸载，会询问是否同时删除配置/登录数据（学习文件夹永远不碰）。

> **SmartScreen 提示（下载后第一次运行会看到蓝色警告框）**：本项目是个人开源软件，未购买代码签名证书，Windows 会显示「已保护你的电脑」。**放行只需一次，30 秒**：
>
> 1. 点弹窗里的「**更多信息**」（文字很小，在「不运行」按钮下方）；
> 2. 点出现的「**仍要运行**」。
>
> 之后安装、升级、日常启动都不会再弹。软件安全性可以自行验证：全部源码公开、无服务器、无遥测（见下方 FAQ）。
> 也可以用 PowerShell 校验文件哈希与 Release 页公布的一致：`Get-FileHash .\FocusDesktop-Setup-1.0.0.exe`

## 首次配置（一次性的图形向导）

第一次启动会进入 4 步向导，全程点选，不需要懂任何配置文件：

1. **学习文件夹** —— 你的资料放哪（PDF/图片/笔记）；默认 `文档\focus`；
2. **网站** —— 勾选预设站点（哔哩哔哩/ChatGPT/AI Studio/DeepSeek），或添加自定义网站（输入网址即可）；只勾选的网站才会出现；
3. **专注设置** —— 首页那句「你想成为怎样的人？」、退出时要输入的句子、番茄钟参数；
4. **背景图**（可选）—— 选一张图，自动居中裁切铺满首页并调暗，不影响文字可读性。

每一步都可以「预览首页」看效果再回来改。点「完成并开始使用」后配置即冻结：**日常界面里没有设置入口**——环境稳定不折腾。想重新配置 = 卸载重装（配置在卸载时清除）。

之后去各网站标签页登录一次（登录态永久保存），点「开始专注」，进入锁定。

## 日常使用

| 想做什么 | 操作 |
|---|---|
| 开始/暂停计时 | 首页按空格 |
| 看网课 | 顶栏「哔哩哔哩」标签 |
| 问 AI | 顶栏 Chat GPT / AI Studio / DeepSeek 标签 |
| 开同站第二个标签 | 标签栏「+」 |
| 看资料 | 「学习文件」→ 点 PDF/图片/TXT（内置打开，不弹外部程序） |
| 番茄钟 | 右侧卡片选时长 → 开始 |
| 硬性专注 | 番茄钟卡片上的开关：开了就关不掉、计时不可重置，唯一出口=本专注段跑完（给「今天完全坐不住」的自己用的） |
| 调音量 | 顶栏滑块 |
| 退出 | 右上角「退出」→ 输入你在向导里设的那句话 |

## 万一出不来了（安全设计）

**任何**退出方式——正常退出、程序崩溃、任务管理器强杀、断电——任务栏都会回来：

- 程序自己恢复：脏标志 + 启动自愈（2 秒内）+ 看门狗伴生进程；
- 双击桌面「**Focus Desk 恢复**」快捷方式（安装版自带；便携版运行 `focus-desktop.exe --restore`）；
- 最坏情况：Ctrl+Alt+Del → 任务管理器永远可用（刻意保留的系统通道）。

你的学习文件夹、网站登录态在任何情况下都不会被程序删除。

## 常见问题

**Q: 装的时候弹「Windows 已保护你的电脑」，有毒吗？**
没有。这是 SmartScreen 对未签名软件的统一拦截（本项目没买代码签名证书）。点「更多信息 → 仍要运行」放行一次即可，之后不再弹。安全性可自行验证：源码全公开，无服务器、无账号、无遥测。

**Q: 它会偷上网/上传数据吗？**
不会。无服务器、无账号、无遥测，所有数据本地（配置 + 浏览器登录态）。

**Q: 忘了退出语怎么办？**
Ctrl+Alt+Del → 任务管理器 → 结束 focus-desktop → 任务栏自动恢复（看门狗）。第二天再进即可，没有任何持久损失。

**Q: 换电脑怎么迁移？**
安装版：新机重装 + 重跑向导 + 重登网站（登录态不可迁移，属安全设计）。便携版：整个文件夹拷走（登录态也在里面）。

**Q: 为什么没有设置中心？**
刻意设计：环境稳定 > 灵活配置。统计上「能随时改的设置」= 学习时一定会去改。

---

# 以下面向开发者

## 技术栈

C# / .NET 10 · WPF + WebView2（WindowsFormsHost 混合承载）· 单工程 · 无服务器/无数据库/无 daemon · MIT。

## 架构速览

```text
App.xaml.cs             启动分流：--preview/--smoke/--restore/--watchdog/--urltest/--voltest；首次运行进配置向导
MainWindow              Tab 条 + 首页（时钟/专注语/快捷入口/背景图）+ 文件页 + Web 宿主
SetupWizard             4 步首配向导：内存草稿 → 原子提交（中途退出零残留）
Services/
  SiteCatalog           站点数据驱动核心：preset/custom 解析、白名单/登录域重算（纯函数，可单测）
  WebTabService         Tab 生命周期/单 Environment 共享登录态/崩溃自愈
  FocusModeService 等   锁定层：WH_KEYBOARD_LL 钩子、任务栏隐藏/恢复、脏标志协议
  WatchdogService       伴生进程：主进程被强杀后 2s 内恢复任务栏
  PomodoroService       番茄钟状态机 + 硬性专注
```

## 构建/发布

```bash
# 日常开发
dotnet run --project src/focus-desktop -- --dev

# 一键出安装包 + 便携 zip（publish → Inno Setup 编译）
powershell -ExecutionPolicy Bypass -File installer/build-release.ps1
```

## 自动验收（改代码必跑对应层）

| 脚本 | 覆盖 |
|---|---|
| `tests/verify-step12.ps1` | 锁定层 13 项（钩子/任务栏/强杀自愈/干净退出/`--restore`），跑时真实锁桌面约 40s |
| `tests/verify-setup.ps1` | 首配向导 32 项（向导全流程/原子提交/中途强杀幂等/legacy 不弹向导） |
| `tests/verify-install.ps1` | 安装包 16 项（静默装/首跑落盘/升级保数据/卸载清数据/学习目录不碰） |
| `--urltest` 参数 | 白名单/站点解析 53 项 |
| `--smoke` 参数 | WebView2 环境 + 全 Tab 创建冒烟 |

## 关键设计决策（踩坑实录）

- **Tab 秒切** = 所有 WebView 常驻不隐藏 + WinForms z 序切换（`Visible=false` 会挂起 Chromium 合成表面 → 恢复必白闪，像素取证定案）；
- **单文件发布必须** `IncludeNativeLibrariesForSelfExtract=true`：否则 WPF 本机 DLL 不进包，装到干净目录首跑 `DllNotFoundException`（portable 目录能跑是历史散文件残留的假绿）；
- **恢复纵深四层**：脏标志自愈 → 看门狗 → `--restore` → explorer 兜底，每层独立可验；
- 配置三态（Draft/Frozen/Legacy）：老用户的 v0.5.x 配置升级后零感知，不弹向导。

## 目录结构

```text
src/focus-desktop/     单工程（Services/ 内分目录）
installer/             Inno Setup 脚本 + 一键构建管线
tests/                 自动验收脚本（PowerShell + 按键注入 + Win32 探针）
assets/                应用图标 + 生成脚本
release/               发布产物（不进 git）
```

## 致谢 / 第三方

详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)：class-lock（键盘钩子移植）、Umbra（原子写入/崩溃报告/生命周期）、Focuser（计时器状态机）——均 MIT；SafeExamBrowser（MPL-2.0，只读参考）。

## 边界（明确不做）

跨平台、多显示器、内容净化/推荐流处理、账号/云同步、插件系统、家长管控、自动更新。
