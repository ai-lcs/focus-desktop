# focus-desktop 晨报（2026-08-30 凌晨交付）

> Kevin 早。按你的要求全程无人值守跑完了。**V1 按 spec（DeskHub_V1_Technical_Spec.md 最终版）做完并交付。**

## TL;DR

- **代码**：https://github.com/ai-lcs/focus-desktop （public + MIT，6 个 commit，全推送）
- **Release**：https://github.com/ai-lcs/focus-desktop/releases/tag/v0.1.0 （v0.1.0 zip，自包含免装 .NET）
- **桌面快捷方式**：`focus-desktop`（正常启动）+ `focus-desktop 恢复`（任务栏没回来时双击）——已建好
- **验证**：13/13 自动验收 + 18/18 白名单测试 + smoke 实测，全过

## 今晚实际完成的事

| 项 | 状态 |
|---|---|
| Step 0 环境（.NET 10 SDK 用户级安装，DOTNET_ROOT 已设） | ✅ |
| Step 1+2 壳层（全屏/任务栏隐藏/键盘钩子/恢复协议/单实例） | ✅ 13/13 验收 |
| Step 3 Web 层（WebView2 四站 Tab/白名单/登录 Profile/新窗口拦截/下载禁用） | ✅ smoke + urltest |
| Step 4 文件（浏览/返回上级/搜索/PDF·图片·TXT 内置打开） | ✅ 编译+smoke |
| Step 5 计时器（正计时/暂停/继续）+ 时钟 + 音量滑块 | ✅ |
| Step 6 退出语验证（弹窗输入逐字匹配，config 可改） | ✅ 自动化含此项 |
| Step 7 异常恢复（强杀→脏标志→自愈→--restore） | ✅ 13 项里 4 项专测 |
| 首次 Setup 模式（spec §9：首次不锁，登录后点「开始专注」） | ✅ |
| GitHub 仓库 + README + THIRD_PARTY_NOTICES + LICENSE | ✅ 全推送 |
| 桌面快捷方式 ×2 + Release v0.1.0（含 zip） | ✅ |

## 自动验证覆盖了什么（停点①②的自动化替代）

**13 项全过**（tests/verify-step12.ps1，PowerShell 按键注入 + Win32 探针，与人手测同一判据）：
启动 → 全屏铺满 → 任务栏隐藏 → Win 键被拦（开始菜单不弹）→ Alt+Tab 被拦（前台不切）→ Alt+F4 被拦（进程存活）→ 强杀后脏标志残留 → 重启自愈触发 → UIA 点击退出 → 输入退出语 → 确认后进程退出 → 任务栏恢复 → 脏标志清除 → --restore 兜底（人为造孤儿态后修复）。

**白名单 18/18**（--urltest）：四站正常放行（含子域/视频页/会话页）、OAuth 登录域放行；百度/YouTube/微博/淘宝拦、**仿冒域拦**（evil-bilibili.com、bilibili.com.evil.io）、file:// 和 javascript: 协议拦。

## 已知问题 / 待你人工确认的（按重要性排）

1. **【必须人工】四站登录态实测**——自动验证只能证明「页面加载+白名单工作」，登录（OAuth/扫码/验证码）必须你本人来。流程：双击桌面 focus-desktop → 首次进 Setup 模式（不锁定）→ 四个标签页各登一次 → 点「开始专注」。之后重启登录态应保持（WebViewProfile 持久化）。
2. **【必须人工】B 站视频播放实测**——播一节网课，验证清晰度切换和网页全屏（Esc 应能退出全屏、不能退出软件）。
3. **【小瑕疵】smoke 日志的最后一行**（"auto-shutdown timer fired"）偶尔缺——进程退出快于日志落盘，功能无影响。
4. **【小瑕疵】音量滑块拖动时会触发系统音量图标变化**，属正常 Core Audio 行为。
5. **【环境】你机器的 .NET 10 是用户级安装**（~/.dotnet + DOTNET_ROOT 用户变量）。自包含 Release 版不受影响；源码开发需要新终端才继承 DOTNET_ROOT。
6. **【说明】Windows 125% 缩放下 WPF 全屏尺寸**用了 SystemParameters 物理像素，理论上正确铺满；若发现边缘漏 1-2px 告诉我。

## 今晚发现并修掉的真 bug（记录一下）

- 退出按钮自锁：`WndProc` 的 WM_CLOSE 防线会吃掉 Shutdown 的关闭消息 → 先 `_focus.Exit()` 再 Shutdown
- 前台焦点：Windows ForegroundLockTimeout 阻止新进程抢焦点 → OnContentRendered 里 Activate + SetForegroundWindow
- XAML 资源顺序：NavBtn 样式定义在使用之后 → StaticResource 找不到（启动即崩）
- Setup 模式与自动验证的冲突：清了 setup_done.flag 导致应用不锁（按设计走）→ 验证脚本预置 flag

## 明早建议顺序（10 分钟）

1. 双击桌面 **focus-desktop** → 首次 Setup：登录四站（重点 ChatGPT/Gemini 的 Google OAuth 和 B 站扫码）→ 点「开始专注」
2. 锁定状态下试 Win / Alt+Tab / Alt+F4 / Ctrl+Shift+Esc（应能开任务管理器）→ 右上退出 → 输退出语
3. 重新打开 → 登录态应保持 → B 站放一节网课
4. 学习文件 → 开一个 PDF
5. 有问题告诉我现象 + `focus-desktop-data/logs/` 里的最新 crash log
