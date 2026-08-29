# focus-desktop

> Windows 单窗口专注学习环境：全屏 kiosk 外壳 + 白名单网页（Bilibili / ChatGPT / Gemini / DeepSeek）+ 学习文件夹 + 内置 PDF + 计时器。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()

## 这是什么

一个自用的「行为摩擦」软件，不是家长管控：

- 启动即进入无边框全屏、隐藏任务栏；拦截 Win / Alt+Tab / Alt+F4 / Alt+Esc / Ctrl+Esc 等常见逃离路径；
- **保留** Ctrl+Alt+Del 和任务管理器（系统级救生通道，故意不拦）；
- 退出需要完整输入预设句子——增加行为摩擦，而不是锁死；
- 内置 WebView2 白名单网页（登录态持久保存）、学习文件夹浏览、PDF 阅读、正/倒计时；
- **任何退出方式**（正常退出 / 崩溃 / 强杀）后，任务栏和系统状态都可恢复：脏标志 + 启动自愈 + `--restore` 兜底。

## 快速开始

```text
release/focus-desktop/   ← 自包含版（免装 .NET，双击即用）
```

桌面快捷方式：
- **focus-desktop** —— 正常启动
- **focus-desktop 恢复** —— 任务栏没回来时双击（纯恢复模式）

## 使用

| 操作 | 方式 |
|---|---|
| 计时器 | Home 页空格：开始 / 暂停 / 继续 |
| 看网课 | 顶栏 Bilibili 标签 |
| 问 AI | 顶栏 ChatGPT / Gemini / DeepSeek 标签 |
| 看资料 | 学习文件 → 点击 PDF/图片/TXT（内置打开） |
| 调音量 | 顶栏滑块 |
| 退出 | 右上角退出 → 输入完整退出语 |

**首次启动**：不锁定，自由登录各网站（登录态会永久保存），点「开始专注」进入锁定模式。之后每次启动直接锁定。

## 命令行参数

| 参数 | 作用 |
|---|---|
| `--dev` | 开发模式：跳过键盘锁与退出语，显示 DEV 角标 |
| `--smoke` | 冒烟测试：初始化全部（含 WebView2），15 秒自动退出 |
| `--restore` | 纯恢复模式：恢复任务栏 + 清脏标志，不进界面 |

## 配置（config.json）

运行数据在 exe 旁 `focus-desktop-data/`（便携布局，勿装进 Program Files）：

```json
{
  "studyFolder": "D:\\Study",                        // 学习目录
  "exitPhrase": "我确实有事需要离开这个环境，我要马上回来。",  // 退出语（可改）
  "whitelist": ["chatgpt.com", "gemini.google.com", "deepseek.com", "bilibili.com"],
  "loginDomains": ["accounts.google.com", "auth.openai.com", "cdn.auth0.com",
                    "passport.bilibili.com", "login.live.com"]  // 登录跳转放行域
}
```

白名单只拦**顶层导航**，不碰 CDN/静态资源，B 站和 AI 站正常工作。新网站加进 `whitelist` 即可。

## 技术栈

C# / .NET 10 · WPF + WebView2（WindowsFormsHost）· 无服务器 / 无数据库 / 无 daemon。

## 开发

```bash
# 日常开发
dotnet run --project src/focus-desktop -- --dev

# 自动验收（键盘锁/任务栏/恢复协议 13 项，约 40 秒，期间真实锁定桌面）
powershell -ExecutionPolicy Bypass -File tests/verify-step12.ps1
```

## 目录结构

```
src/focus-desktop/          # 单工程（Views/Services 内分目录，不拆多工程）
  Services/                 # 键盘钩子/任务栏/恢复/白名单/WebView Tab/音量/配置
tests/                      # 自动验收脚本（PowerShell + 按键注入 + Win32 探针）
release/focus-desktop/      # 自包含发布版（不进 git）
```

## 可靠性设计

- **恢复协议**：进入锁定先写脏标志（原子写入）→ 任何退出路径（正常/异常/强杀）都汇入同一条恢复代码 → 下次启动发现脏标志自动恢复 → `--restore` 手动兜底 → explorer 重启最后防线；
- **键盘钩子**：`WH_KEYBOARD_LL` 全局低级钩子（WebView2 是独立子 HWND，WPF 键事件看不见，只有 LL 钩子跨进程有效）；回调只做查表，极快返回；
- **Esc 永远不拦**：B 站网页全屏退出依赖它；
- **单实例互斥**：防双开造成双重钩子/双重隐藏。

## 致谢 / 第三方

详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)：class-lock（键盘钩子移植）、Umbra（原子写入/崩溃报告/生命周期）、Focuser（计时器状态机）——均 MIT；SafeExamBrowser（MPL-2.0，只读参考）；PDF.js（如启用）。

## V1 边界（明确不做）

跨平台、多显示器、B 站内容净化、推荐流处理、账号/云同步、服务器、数据库、daemon、浏览器插件、家长管控。
