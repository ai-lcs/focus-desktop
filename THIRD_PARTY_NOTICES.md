# Third-Party Notices

本项目参考、借鉴或移植了以下开源项目的代码与设计。感谢这些项目的作者。

## 直接移植的代码（文件头注释均标注来源）

### class-lock — https://github.com/ExplorerAC/class-lock
- License: MIT (Copyright © 2026 ExplorerAC)
- 移植内容: `Services/KeyboardGuardService.cs` — `WH_KEYBOARD_LL` 低级键盘钩子的拦截结构与 VK 常量表，由 Python (ctypes) 翻译为 C# P/Invoke。

### Umbra — https://github.com/zixload/umbra
- License: MIT (Copyright © zixload)
- 移植内容:
  - `Services/AtomicFile.cs` — 原子文件写入（tmp + Move）
  - `Services/CrashReporter.cs` — 崩溃日志（截断保护 + 数量裁剪）
  - `Services/AppSettings.cs` / `Services/Paths.cs` — 配置与路径表的组织模式
- 借鉴（未复制）: 应用启动参数分发、Focus Session 生命周期、三层异常接线（DispatcherUnhandledException / AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException）。

### Focuser — https://github.com/aadeshrao123/Focuser
- License: MIT (Copyright © aadeshrao123)
- 借鉴（翻译而非复制）: `pomodoro.rs` 的计时器状态机模型（phase_started_at 时间戳 + paused_remaining_secs 冻结剩余秒数）。

## 只读参考（未复制代码）

### SafeExamBrowser — https://github.com/SafeExamBrowser/seb-win-refactoring
- License: MPL-2.0
- 参考内容: `KeyboardInterceptor.cs` 的拦截键位清单与判断结构（Alt 系 / Win 系 / Injected 标志）。MPL-2.0 为文件级 copyleft，本项目未复制其任何源代码文件。

### WebView2Samples — https://github.com/MicrosoftEdge/WebView2Samples
- 无 LICENSE 文件（默认版权保留）。本项目未复制其任何代码。WebView2 功能通过 NuGet 包 `Microsoft.Web.WebView2`（其自带许可证）使用。

## 运行时组件

- **WebView2 Runtime**（Microsoft，随 Windows 分发）— 网页渲染。
- **PDF 查看器**：V1 使用 WebView2 内置 PDF 查看能力，未捆绑 PDF.js；若后续切换，将在本文件补充 Apache-2.0 NOTICE。

## Dock

Dock 使用 Windows 自带的 AppBar、Shell 和应用激活 API；本功能没有新增第三方源代码或 NuGet 依赖。


## Markdown 与系统接口

### Markdig 1.3.2

https://github.com/xoofx/markdig — BSD-2-Clause。用于本地 Markdown 排版；禁用原始 HTML。

Copyright (c) 2018-2019, Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without modification
, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

### System.Management 10.0.0

https://github.com/dotnet/runtime — MIT。用于 Windows WMI 内屏亮度读写。许可证随 NuGet 包提供。

The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

