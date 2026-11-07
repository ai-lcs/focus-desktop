# 测试说明

## 本轮稳定性测试

新版 Dock 安装身份、默认安装目录和安装版数据路径已经独立于旧版；旧安装隔离脚本仍含历史 AppId 假设，禁止用于本轮宿主机验收。

`RuntimeFlow` 在自己的构建目录创建 portable.flag、合成 profile 和可丢弃全屏软件窗口，运行两个本地 WebView、文件页/首页与外部窗口往返，检查草稿、Cookie、页面实例、计时/Hard Focus 及固定栏保留。默认不操作任务栏或注册 AppBar/键盘钩子。结果不能代替真实鼠标、第三方软件或安装验收。它会显示测试窗口，仅在独立测试环境运行：

```powershell
dotnet run --project tests/RuntimeFlow
```

`-- --formal` 会短暂隐藏任务栏、注册真实左侧/顶部 AppBar 和键盘钩子，使用生产恢复与看门狗验证。仅在允许接管桌面的独立测试会话运行，禁止并行运行。

`--existing-app=<完整 exe 路径>` 只选取已经运行的指定程序，不会启动或关闭它；该模式会临时调整其窗口。`--observe` 在切换前等待 60 秒，以便向合成主窗口提供真实前台输入；`--observe-external` 在第一次外部窗口切换后停留 60 秒供截图检查。两者都不会把直接调用处理函数的测试变为真实鼠标点击验收。

1.2.4 增加 `--reveal-regression`：对系统任务栏调用 20 次显示原语，检查专注期间透明度与底边命中穿透，并在退出时核对原样式和快照清理。`--edge-input` 使用测试 profile 中的 `edge-main-ready` / `edge-start`、`edge-external-ready` / `edge-done` 文件同步真实输入阶段；继续信号内容必须等于本次测试 PID。每阶段最多等待 10 分钟，总安全时限 15 分钟，正常完成信号到达后立即继续。可丢弃窗口记录底边的原生移动和点击消息至构建目录的 `edge-input-events.jsonl`。仅在完成真实输入后才发出结束信号；不得把超时或中断记为通过。实际证据见 [1.2.4 验收报告](../docs/acceptance/dock-1.2.4-taskbar-reveal-2026-11-07.md)。

1.2.3 本机验收覆盖两栏与底部边界、40 次真实 QQ 音乐往返、160 次网页/文件/首页切换、外部窗口原位置还原，以及最终包的正常退出和两阶段异常恢复。记录见 [本机验收报告](../docs/acceptance/dock-1.2.3-local-2026-11-07.md)。安装/升级/卸载矩阵仍需一次性环境。

旧返回热键测试已移除。默认测试不操作任务栏；正式模式结束后确认任务栏与两个 AppBar 的工作区恢复。

## 1.2.1 修复候选的边界

本轮仅处理新版。旧版安装、便携目录、快捷方式、真实 LocalAppData 数据和同 AppId 注册项只读核对，禁止在宿主机运行下面历史安装隔离流程移动真实数据。Setup 的 CURRENTUSER 和默认管理员矩阵使用一次性环境，见 [INSTALLER_TEST_ISOLATION.md](INSTALLER_TEST_ISOLATION.md)。没有安全隔离环境时，该矩阵仍记为未验证，不得记为通过。此节记录 1.2.1 的历史候选门槛；2026-11-08 按用户明确决定，将 1.2.4 作为主版本及 Latest 发布，实际范围和保留缺口以 [1.2.4 发布说明](../docs/releases/v1.2.4.md)为准。

新增的独立逻辑验证：

```powershell
dotnet run --project tests/ProfileLeaseProbe/ProfileLeaseProbe.csproj
dotnet run --project tests/DesktopSession/DesktopSession.Tests.csproj
powershell -File tests/verify-uninstall-data-policy.ps1
```

ProfileLeaseProbe 只在随机临时目录中创建合成文件，验证跨进程互斥、大小写、独立 profile，以及实际删除期间仍拒绝另一实例。DesktopSession 使用独立随机命名 mutex，不触碰任务栏；卸载策略矩阵调用生产身份判据，不读取真实 profile。这些用例不能替代真实桌面或最终 Setup 验收。

最终包的 GUI/恢复证据必须记录运行 exe 的 SHA-256。真实 Dock 点击需要受支持的鼠标输入与有效截图；Button.RaiseEvent、UIA Invoke、纯逻辑测试单独记录。普通非最小化迟到窗口应记录夹具 Show/Activated 时间与前台变化，区分产品回调和应用自行激活；保留旧最小化场景。

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
