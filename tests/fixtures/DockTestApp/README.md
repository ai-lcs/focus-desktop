# Dock 测试应用

这是一个可丢弃的普通 WPF/Win32 测试程序，用于检查 Dock 切换和窗口恢复。它不调用 Focus Desk 配置、登录态或用户项目。

在仓库根目录构建：

```powershell
$dotnet = "C:\Users\LCS\.dotnet\dotnet.exe"
& $dotnet build .\tests\fixtures\DockTestApp\DockTestApp.csproj -c Debug
```

从含中文和空格的工作目录启动，并把报告写入临时目录：

```powershell
$exe = (Resolve-Path .\tests\fixtures\DockTestApp\bin\Debug\net10.0-windows\focus-desk-dock-test-fixture.exe).Path
$work = Join-Path $env:TEMP "Focus Desk 测试 工作目录"
$reports = Join-Path $env:TEMP "Focus Desk Dock 测试 输出"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$arguments = '--fixture-window-count 2 --fixture-start-delay-ms 1800 --fixture-spawn-child --fixture-report-dir "{0}"' -f $reports
Start-Process -FilePath $exe -WorkingDirectory $work -ArgumentList $arguments
```

每个窗口启动时会在报告目录写入 JSON，记录 PID、可执行文件路径、当前工作目录、父 PID、启动参数和窗口序号；不会记录窗口标题以外的用户内容。可在文本框中输入测试内容，按 Alt+F4 会出现保存、放弃或继续编辑的模态对话框。保存文件只写到报告目录。`--fixture-spawn-child` 会启动一个独立子进程窗口，用于确认主窗口退出后该子进程仍存活。

可用参数：

- `--fixture-window-count N`：同一进程打开 1 到 5 个主窗口。
- `--fixture-start-delay-ms N`：延迟显示窗口，范围 0 到 60000 毫秒。
- `--fixture-start-minimized`：以不激活的最小化状态显示窗口，用于验证过期启动意图不会抢回焦点。
- `--fixture-spawn-child`：另外启动一个独立子进程窗口。
- `--fixture-report-dir PATH`：指定报告和保存文件目录；省略时使用 `%TEMP%\Focus Desk Dock Test Fixture`。

该夹具只用于人工桌面验证。它不会自行安装、查杀进程或修改任务栏。
