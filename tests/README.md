# 测试说明

本目录里的旧验收脚本并非都适合在当前机器直接运行。运行前先读脚本并确认它写入、清理和启动的确切路径。本文只记录当前可用的隔离准备工具；此时新增 Dock 的包级安装、桌面往返和恢复验收尚未完成，不代表测试通过。

## 旧脚本风险

不要直接运行以下旧脚本：

| 脚本 | 已发现的影响 |
| --- | --- |
| `verify-install.ps1` | 通过同一 AppId 选择注册表卸载项；删除 `%LOCALAPPDATA%\focus-desktop\config.json`、`setup_done.flag`；按进程名结束 Focus Desk 和 WebView2；写入固定的 `D:\focus-desktop\tests\fixture-study`。 |
| `verify-step12.ps1` | 使用硬编码的 `D:\focus-desktop` 路径；按进程名结束 Focus Desk/看门狗；写数据、隐藏任务栏并做 UI 输入。 |
| `verify-setup.ps1` | 读写真实 LocalAppData 配置和首次设置标记；会按进程终止窗口并用固定临时备份恢复。 |
| `verify-tabs.ps1` | 启动硬编码的旧 Release 路径，并按进程名结束 Focus Desk。 |
| `verify-watchdog.ps1` | 操作运行数据和看门狗文件；会结束进程，并在清理时删除固定备份目录。 |

这些脚本保留在仓库中供后续单独修订；本轮不以它们的输出作为验收证据。

## Dock 窗口夹具

`fixtures/DockTestApp` 是可丢弃的普通 WPF/Win32 程序。构建不会启动应用，也不会改系统状态：

```powershell
$dotnet = 'C:\Users\LCS\.dotnet\dotnet.exe'
& $dotnet build .\tests\fixtures\DockTestApp\DockTestApp.csproj -c Debug
```

把测试报告、保存内容放在临时目录，并用含中文及空格的工作目录启动：

```powershell
$exe = (Resolve-Path .\tests\fixtures\DockTestApp\bin\Debug\net10.0-windows\focus-desk-dock-test-fixture.exe).Path
$work = Join-Path $env:TEMP 'Focus Desk 测试 工作目录'
$reports = Join-Path $env:TEMP 'Focus Desk Dock 测试 输出'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$arguments = '--fixture-window-count 2 --fixture-start-delay-ms 1800 --fixture-spawn-child --fixture-report-dir "{0}"' -f $reports
Start-Process -FilePath $exe -WorkingDirectory $work -ArgumentList $arguments
```

可在测试窗口输入临时文字；Alt+F4 会显示保存/放弃/继续编辑对话框。夹具支持多个主窗口、延迟启动和独立子进程，保存内容仅写入显式指定的报告目录。测试结束后通过窗口自己的关闭流程退出；不要按进程名批量结束。

## 只读窗口快照

`inspect-dock-windows.ps1` 仅读取窗口句柄、PID、窗口矩形、置顶状态、进程名和应用身份，不采集窗口标题或命令行；可选 JSON 输出文件应放在隔离证据目录。脚本以临时线程 DPI 感知上下文读取物理像素，并在 `finally` 恢复原上下文。它不操作窗口、AppBar、任务栏或进程。主控已修复并实跑过该快照工具；这不等于 Dock 运行验收。

## 入口解析探针

`EntryProbe` 默认只解析一个明确指定的 `.lnk` 或 `.exe`，输出类型、稳定 ID、AUMID 和可执行文件路径；不会启动应用，也不在控制台打印参数或工作目录。`--out=<路径>` 会以“仅新建”方式写完整入口 JSON，文件中包含启动参数和工作目录，应只对明确的测试快捷方式使用并保存在私有证据目录。只有显式传 `--launch` 才会启动该入口：

```powershell
$dotnet = 'C:\Users\LCS\.dotnet\dotnet.exe'
& $dotnet run --project .\tests\EntryProbe\EntryProbe.csproj -- --resolve 'D:\path\测试编辑器.lnk' '--out=D:\path\fixture-entry.json'
```

带 `--launch` 会真实调用 Windows Shell/应用激活，运行前必须确认路径与目标应用；本说明不表示已运行该命令。

## 隔离安装验收脚本

`verify-install-isolated.ps1` 设计为同一精确 Setup 包的串行流程。预检和首次安装要求调用者传入本机已核实的绝对 `InstallLocation`；后续阶段保存并核对首次预检的注册表快照指纹：

```powershell
$expectedMachineInstall = Read-Host '输入经人工确认的原 HKLM Focus Desk InstallLocation'
$testRoot = Join-Path (Get-Location) 'evidence\focus-desk-install-tests\run-01'
& .\tests\verify-install-isolated.ps1 -Action Preflight -TestRoot $testRoot -ExpectedMachineInstallPath $expectedMachineInstall
```

`Preflight → Install →（人工运行并退出隔离应用）→ Upgrade → UninstallKeep → Reinstall → UninstallClear → Restore`

脚本使用相同 AppId，但通过 `/CURRENTUSER`、明确 `/DIR=<工作区证据目录>\app`、`/NOCLOSEAPPLICATIONS` 和 `/NORESTARTAPPLICATIONS` 安装当前用户副本。它在安装前记录 HKLM 卸载项指纹、快捷方式和真实 LocalAppData 数据树；复制并验证真实数据证据后，才把该目录暂时移到同一 LocalAppData 下的唯一备份路径。它不卸载 HKLM 正式安装、不按名称结束进程。恢复时保留测试数据证据，恢复原备份并核对整棵数据树哈希，快捷方式和 HKLM 状态也会核验。

Upgrade、Keep-data 卸载、同包 Reinstall 和 Clear-data 卸载之间会核验测试数据树；每次卸载和恢复用户数据前会重新确认 Focus Desk 与引用该数据目录的 WebView2 进程已退出。数据树仍在使用时脚本会停止，不会按进程名结束它们。

运行此流程会暂时移动真实 `%LOCALAPPDATA%\focus-desktop`，因此只有主控审查脚本、确认 Setup 路径与 SHA-256、选定独占的新证据目录并显式传 `-ConfirmProfileIsolation` 后才能开始。安装时还要提供同一个已核实路径和精确 Setup 哈希。任何阶段失败都应先用同一 `-TestRoot` 执行 `Restore`；不要删除脚本生成的原始离线备份或证据。该流程尚未在本轮执行。
