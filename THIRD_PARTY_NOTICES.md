# Third-Party Notices

本项目参考、借鉴或移植了以下开源项目的代码与设计。感谢这些项目的作者。

## 直接移植的代码（含注释标注来源）

### class-lock — https://github.com/ExplorerAC/class-lock
- License: MIT (Copyright © 2026 ExplorerAC)
- 移植内容: `KeyboardGuardService.cs` — `WH_KEYBOARD_LL` 低级键盘钩子的拦截结构与 VK 常量表，由 Python (ctypes) 翻译为 C# P/Invoke。

### Umbra — https://github.com/zixload/umbra
- License: MIT (Copyright © zixload)
- 移植内容:
  - `AtomicFile.cs` — 原子文件写入（tmp + Move）
  - `CrashReporter.cs` — 崩溃日志（截断保护 + 数量裁剪）
  - `AppSettings.cs` / `AppPaths.cs` — 配置与路径表的组织模式
- 借鉴（未复制）: 应用启动参数分发、Focus Session 生命周期、三层异常接线。

### Focuser — https://github.com/aadeshrao123/Focuser
- License: MIT (Copyright © aadeshrao123)
- 借鉴（翻译而非复制）: `pomodoro.rs` 的计时器状态机模型（phase_started_at 时间戳 + paused_remaining_secs 冻结剩余秒数）。

## 只读参考（未复制代码）

### SafeExamBrowser — https://github.com/SafeExamBrowser/seb-win-refactoring
- License: MPL-2.0
- 参考内容: `KeyboardInterceptor.cs` 的拦截键位清单与判断结构。MPL-2.0 为文件级 copyleft，本项目未复制其任何源代码文件。

### WebView2Samples — https://github.com/MicrosoftEdge/WebView2Samples
- 无 LICENSE 文件（默认版权保留）。本项目未复制其任何代码，仅作 API 用法参考。WebView2 功能通过 NuGet 包 `Microsoft.Web.WebView2`（其自带许可证）使用。

## 计划采用（后续 Step）

### PDF.js — https://github.com/mozilla/pdf.js
- License: Apache-2.0。V1 当前使用 WebView2 内置 PDF 查看器；若后续切换到 PDF.js，将在此补充 NOTICE。
