# focus-desktop

> Windows 单窗口专注学习环境：全屏 kiosk 外壳 + 白名单网页（ChatGPT / Gemini / DeepSeek / Bilibili）+ 学习文件夹 + 计时器。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## 这是什么

一个自用的「行为摩擦」软件，不是家长管控：

- 启动即进入无边框全屏，隐藏任务栏，拦截 Win / Alt+Tab / Alt+F4 等常见逃离路径；
- 保留 Ctrl+Alt+Del 系统紧急出口（技术上也无法拦截）；
- 退出需要完整输入预设句子，增加行为摩擦而非锁死；
- 内置 WebView2 白名单网页、学习文件夹浏览、PDF 阅读、计时器；
- 任何退出方式（正常 / 崩溃 / 强杀）后，任务栏和系统状态都可恢复。

## 技术栈

C# / .NET 10 · WPF · WebView2 · 无服务器 / 无数据库 / 无 daemon。

## 开发

```bash
# 日常开发（跳过键盘锁与退出文本）
dotnet run --project DeskHub.App -- --dev

# 纯恢复模式（任务栏没回来时双击这个）
focus-desktop.exe --restore
```

## 目录

```
DeskHub.App/        # 单工程：Views / Services 就内分文件夹
DeskHubData/        # 运行时生成：config.json + WebView Profile + 日志（不进 git）
```

## 致谢 / 第三方

本项目的部分实现参考或移植自以下 MIT / Apache-2.0 项目，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)：

- [class-lock](https://github.com/ExplorerAC/class-lock)（MIT）— 低级键盘钩子
- [Umbra](https://github.com/zixload/umbra)（MIT）— 原子写入、崩溃报告、应用生命周期
- [Focuser](https://github.com/aadeshrao123/Focuser)（MIT）— 计时器状态机
- [SafeExamBrowser](https://github.com/SafeExamBrowser/seb-win-refactoring)（MPL-2.0，只读参考）— 拦截策略校对
