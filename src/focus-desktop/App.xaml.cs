using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using focus_desktop.Services;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace focus_desktop;

public partial class App : System.Windows.Application
{
    private DesktopSessionLease? _desktopSessionLease;
    private ProfileUsageLease? _profileUsageLease;
    private AppOptions _options = null!;
    private FocusModeService _focus = null!;

    /// <summary>全局焦点模式服务（异常兜底和主窗口都要访问）。</summary>
    internal FocusModeService Focus => _focus;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _options = AppOptions.Parse(e.Args);

        // This check runs as the original interactive user, rather than the elevated installer account.
        if (e.Args.Contains("--first-run-if-needed", StringComparer.OrdinalIgnoreCase)
            && Directory.Exists(Paths.DataDir))
        {
            Shutdown();
            return;
        }

        // The isolated Dock self-test owns its unobserved-task handler and must run before app handlers.
        if (_options.DockSelfTest)
        {
            var code = Tests.DockSelfTest.Run();
            Shutdown();
            Environment.Exit(code);
            return;
        }

        // This must run before the singleton check so an existing instance can receive the shutdown request.
        if (e.Args.Contains("--prepare-uninstall"))
        {
            Environment.Exit(PrepareForUninstall(e.Args.Contains("--remove-user-data", StringComparer.OrdinalIgnoreCase)));
            return;
        }

        WireGlobalExceptionHandlers();

        // Watchdog recovery is bound to one parent identity and session generation.
        if (e.Args.Length > 0 && e.Args[0] == "--watchdog")
        {
            if (e.Args.Length != 4 || !int.TryParse(e.Args[1], out var parentPid) || parentPid <= 0
                || !long.TryParse(e.Args[2], out var parentStartTicks) || parentStartTicks <= 0
                || !Guid.TryParseExact(e.Args[3], "N", out _))
            {
                Environment.Exit(2);
                return;
            }
            Environment.Exit(WatchdogService.RunLoop(parentPid, parentStartTicks, e.Args[3]));
            return;
        }

        // --urltest：白名单逻辑自测（无 UI，控制台输出即退）
        if (e.Args.Contains("--urltest"))
        {
            var code = Tests.UrlSelfTest.Run();
            Shutdown();
            Environment.Exit(code);
            return;
        }

        // --voltest：音量 COM 通道自测（无 UI，控制台输出即退）
        if (e.Args.Contains("--voltest"))
        {
            VolumeHelper.Init();
            Console.WriteLine($"IsReady: {VolumeHelper.IsReady}");
            Console.WriteLine($"LastError: {VolumeHelper.LastError ?? "(none)"}");
            var before = VolumeHelper.Get();
            VolumeHelper.Set(before >= 50 ? 30 : 70);
            System.Threading.Thread.Sleep(300);
            var after = VolumeHelper.Get();
            Console.WriteLine($"Get before={before} -> Set -> Get after={after}");
            Console.WriteLine(after == (before >= 50 ? 30 : 70) ? "VOLTEST PASS" : "VOLTEST FAIL");
            Console.WriteLine($"Mute: {VolumeHelper.IsMuted()}");
            Shutdown();
            Environment.Exit(after == (before >= 50 ? 30 : 70) ? 0 : 1);
            return;
        }

        // --restore：纯恢复模式。不进 UI，恢复完就退。
        if (_options.Restore)
        {
            if (!DesktopSessionCoordinator.TryAcquire(out var restoreLease, out var ownershipReason))
            {
                WpfMessageBox.Show(ownershipReason, "Focus Desk 恢复未执行", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
                Environment.Exit(1);
                return;
            }
            using var desktopLease = restoreLease;
            if (!ProfileUsageLease.TryAcquire(Paths.DataDir, out var profileLease, out ownershipReason))
            {
                WpfMessageBox.Show(ownershipReason, "Focus Desk 恢复未执行", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
                Environment.Exit(1);
                return;
            }
            using var restoreProfileLease = profileLease;
            if (!RecoveryService.TryGetRecoverableSession(out var recoverySession, out var recoveryReason))
            {
                WpfMessageBox.Show(recoveryReason, "Focus Desk 恢复未执行", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
            var restored = recoverySession is null || TaskbarService.Show();
            var recoveryConfirmed = RecoveryService.TryConfirmRecovery(restored, recoverySession, out recoveryReason);
            if (recoveryConfirmed) recoveryConfirmed = RecoveryService.TryMarkCleanIfCurrent(recoverySession);
            var message = recoverySession is null
                ? "没有待恢复的 Focus Desk 会话，本次未修改桌面。"
                : recoveryConfirmed
                    ? "任务栏与 AppBar 恢复已确认，已清除本次会话的恢复标记。"
                    : $"恢复未能全部确认，标记已保留。请先正常退出其他 Focus Desk 实例，检查任务栏与显示器工作区后再重试 --restore。\n\n{recoveryReason}";
            WpfMessageBox.Show(message, "Focus Desk 恢复",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            Shutdown(recoveryConfirmed ? 0 : 1);
            return;
        }

        // 单实例：防止双击两次造成双重钩子/双重隐藏的混乱状态
        if (!DesktopSessionCoordinator.TryAcquire(out _desktopSessionLease, out var sessionReason))
        {
            WpfMessageBox.Show(sessionReason, "focus-desktop",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (!ProfileUsageLease.TryAcquire(Paths.DataDir, out _profileUsageLease, out var profileReason))
        {
            WpfMessageBox.Show(profileReason, "Focus Desk 配置正在使用", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _focus = new FocusModeService();

        // 自愈：上次非正常退出（脏标志残留）→ 先恢复系统状态再继续启动
        if (RecoveryService.WasActiveLastTime())
        {
            CrashReporter.Write(new InvalidOperationException("上次会话残留 focus_mode_active=true，开始尝试自愈恢复"),
                "startup-self-heal");
            _focus.Recover();
            if (RecoveryService.WasActiveLastTime())
            {
                RecoveryService.TryConfirmAppBarReleased(null, null, out var recoveryReason);
                WpfMessageBox.Show(
                    $"上次 Focus Desk 会话的 AppBar 恢复状态无法确认。任务栏已尽力恢复，恢复标记仍保留，Focus Desk 不会进入锁定模式。\n\n{recoveryReason}\n\n请恢复原显示器布局并释放同一边缘的其他桌面栏，再运行 --restore 重试。",
                    "Focus Desk 恢复未完成", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
                Shutdown();
                return;
            }
        }

        // 配置：新安装写入 configured:false，避免默认白名单被误判为 legacy。
        var settings = AppSettings.LoadOrDefault(out var configStatus);
        if (configStatus == AppSettings.ConfigLoadStatus.LegacyIdentityConflict)
        {
            WpfMessageBox.Show(
                $"旧版应用名单含有同一应用的多个启动定义，新版无法将它们区分为独立窗口入口。配置和网页登录数据均未修改，本次不会进入专注。\n\n{Paths.ConfigFile}\n\n请先备份该配置文件，在程序退出后仅调整 externalApps：每个 exe 或 AUMID 保留一个需要的入口，保留该入口的完整 ID、参数与工作目录。其他配置和 WebViewProfile 无需删除。重新启动前请核对备份。",
                "Focus Desk 旧版名单需要确认", WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
            Shutdown();
            return;
        }
        if (configStatus == AppSettings.ConfigLoadStatus.Corrupt)
        {
            WpfMessageBox.Show(
                $"配置文件无法读取或外部应用名单校验失败。Focus Desk 未修改该文件，也不会进入锁定或默认配置向导。\n\n{Paths.ConfigFile}\n\n请先恢复备份或手动修复配置，再重新启动。",
                "Focus Desk 配置损坏", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
            Shutdown();
            return;
        }
        else if (configStatus == AppSettings.ConfigLoadStatus.Missing)
        {
            settings.Configured = false;
            settings.Save();
        }

        var main = new MainWindow(_options, _focus);
        MainWindow = main;
        main.ConfigureDock(settings, !_options.Dev || _options.DockAppBar);

        if (_options.Smoke)
        {
            SmokeLog("startup: smoke mode (no focus lock)");
            // 自动退出 timer 在 MainWindow.InitAsync 里（15 秒，含 Web 层初始化验证）
        }

        // 锁定策略：--smoke 永不锁；--preview 预览模式（不锁+普通窗口+直接退出）；
        // 首次配置（Configured != true 且非 legacy 老配置）→ 显示配置向导层（不锁）；
        // configured=true 但 setup_done 缺失 → 保持登录引导态，不提前锁定；
        // legacy 配置视为已配置并直接锁定。
        if (!_options.Dev && !_options.Smoke && !_options.Preview)
        {
            var legacy = settings.IsLegacyConfig();
            if (!settings.IsConfigured() && !legacy)
            {
                ShowSetupWizard(settings);
                App.SmokeLog("first-run setup wizard mode (main hidden, no lock)");
                return;
            }

            if (settings.ExternalAppsConfirmed == null && settings.ExternalApps == null)
            {
                ShowExternalAppsSupplement(settings, main, legacy);
                return;
            }

            var stagedDock = (legacy || FirstRunSetup.IsSetupComplete()) && settings.ExternalApps is { Count: > 0 };
            if (stagedDock)
            {
                WindowTransitions.SetVisible(main, false);
                main.PrepareForAppBarDock();
            }
            main.Show();
            if (!legacy && !FirstRunSetup.IsSetupComplete())
            {
                main.ShowLoginHint();
                App.SmokeLog("configured setup pending; login guidance mode (no lock)");
            }
            else
            {
                try { await _focus.EnterAsync(); WindowTransitions.SetVisible(main, true); } // Final bounds before first visible frame.
                catch (Exception ex)
                {
                    main.ReturnToFocusDesk();
                    main.ReportFocusRecoveryError($"无法进入专注模式：{ex.Message}");
                }
            }
        }
        else
        {
            main.Show();
            if (_options.Dev && !_options.Smoke && !_options.Preview
                && settings.ExternalAppsConfirmed == true && settings.ExternalApps is { Count: > 0 })
            {
                if (!_focus.ShowDevelopmentDock(_options.DockAppBar))
                    main.ReportFocusRecoveryError("Dock 原型无法注册或显示。");
            }
        }
    }

    /// <summary>Public v1 首次配置向导：独立普通窗口（MainWindow 保持隐藏；不保存、不进锁定）。</summary>
    private void ShowSetupWizard(AppSettings draft)
    {
        var wizard = new SetupWizard(draft, (MainWindow)MainWindow);
        wizard.Completed += OnSetupWizardCompleted;
        wizard.Show();
    }

    private void ShowExternalAppsSupplement(AppSettings original, MainWindow main, bool legacy)
    {
        var supplement = new AppSupplementWindow(original);
        supplement.Completed += async updated =>
        {
            try
            {
                main.RefreshDockSettings(updated); // 只刷新 Dock 名单，不重建网页或 WebView
                main.Show();
                if (!legacy && !FirstRunSetup.IsSetupComplete())
                {
                    main.ShowLoginHint();
                    return;
                }

                try { await _focus.EnterAsync(); }
                catch (Exception ex)
                {
                    main.ReturnToFocusDesk();
                    main.ReportFocusRecoveryError($"无法进入专注模式：{ex.Message}");
                }
            }
            catch (Exception ex)
            {
                CrashReporter.Write(ex, "external-app-supplement-complete");
                main.Show();
                main.ReportFocusRecoveryError($"应用名单已保存，但无法继续启动：{ex.Message}");
            }
        };
        supplement.Show();
    }

    /// <summary>向导完成（config 已原子写入）→ 进入下一步：登录引导横幅。</summary>
    private void OnSetupWizardCompleted(AppSettings finalConfig)
    {
        if (MainWindow is MainWindow main)
        {
            try
            {
                main.NotifyConfigCommitted(); // F1：首配轮预热延迟到此刻——最终配置已落盘，新 WebView 捕获新策略
                main.RefreshDockSettings(finalConfig);
                main.Show();
                main.ShowLoginHint(); // MainWindow 公开方法（直调，反射方案已否决——编译期可见性优于运行时绑定）
            }
            catch (Exception ex)
            {
                CrashReporter.Write(ex, "wizard-complete-hint");
            }
        }
        App.SmokeLog("setup wizard completed; login hint shown");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 仅启动过焦点服务的实例拥有恢复责任；自测、恢复模式和第二实例不得改桌面状态。
        if (_focus is not null && _focus.HasRecoveryResponsibility)
        {
            try { _focus.ExitIfActive(); }
            catch (Exception ex) { CrashReporter.Write(ex, "on-exit"); }

        }

        _profileUsageLease?.Dispose();
        _desktopSessionLease?.Dispose();
        base.OnExit(e);
    }

    private static int PrepareForUninstall(bool removeUserData)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)) return 1;

        foreach (var process in GetProcessesAtPath(executablePath, Environment.ProcessId))
        {
            using (process)
            {
                if (!TryFindMainWindow(process.Id, out var hwnd)) continue;
                if (!IsFocusDeskMainWindow(hwnd)) continue;

                    PostMessage(hwnd, focus_desktop.MainWindow.WmPrepareUninstall, IntPtr.Zero, IntPtr.Zero);
                if (process.WaitForExit(10000)) continue;

                // A non-responsive or drafting instance must be left intact.
                return 1;
            }
        }

        if (!WaitForNoOwnedProcesses(executablePath, WatchdogService.WatchdogExecutablePath,
                Environment.ProcessId, TimeSpan.FromSeconds(5)))
            return 1;

        if (!DesktopSessionCoordinator.TryAcquire(out var desktopLease, out _)) return 1;
        using var ownedDesktop = desktopLease;
        if (!ProfileUsageLease.TryAcquire(Paths.DataDir, out var profileLease, out _)) return 1;
        using var ownedProfile = profileLease;

        if (RecoveryService.WasActiveLastTime())
        {
            if (!RecoveryService.TryGetRecoverableSession(out var recoverySession, out _)) return 1;
            var restored = TaskbarService.Show();
            if (!RecoveryService.TryConfirmRecovery(restored, recoverySession, out _)
                || !RecoveryService.TryMarkCleanIfCurrent(recoverySession)) return 1;
        }
        var webViewProfile = Path.GetFullPath(Path.Combine(Paths.DataDir, "WebViewProfile"));
        if (!WaitForNoProfileWebViewProcesses(webViewProfile, TimeSpan.FromSeconds(15)))
        {
            CrashReporter.Write(new IOException("无法确认 Focus Desk WebView2 profile 已释放；保留数据并阻止卸载。"),
                "prepare-uninstall-webview-profile-active");
            return 1;
        }
        // Only the exiting main process can confirm that its AppBar and keyboard hooks
        // were released. Taskbar visibility alone is not enough to clear its marker.
        if (RecoveryService.WasActiveLastTime()) return 1;
        if (!removeUserData) return 0;
        if (!UninstallDataPolicy.CanRemoveUserData(Paths.DataDir, out var cleanupReason))
        {
            Console.Error.WriteLine(cleanupReason);
            return 1;
        }
        try
        {
            // Both guards remain held until deletion finishes; Inno must never delete this later.
            Directory.Delete(Paths.DataDir, recursive: true);
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"用户数据清理未完成，可能已部分清除（{ex.GetType().Name}）；卸载应中止。");
            return 2;
        }
    }

    private static bool HasSamePathWindowProcess(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        foreach (var process in GetProcessesAtPath(executablePath, Environment.ProcessId))
        {
            using (process)
            {
                if (TryFindAnyTopLevelWindow(process.Id, out _)) return true;
            }
        }
        return false;
    }

    private static List<Process> GetProcessesAtPath(string executablePath, int excludeProcessId)
    {
        var matches = new List<Process>();
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName)) return matches;

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.Id != excludeProcessId && IsSameExecutable(process, executablePath))
                    matches.Add(process);
                else
                    process.Dispose();
            }
            catch
            {
                process.Dispose();
            }
        }
        return matches;
    }

    private static bool IsSameExecutable(Process process, string executablePath)
    {
        try
        {
            var actualPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actualPath)
                && string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool WaitForNoOwnedProcesses(string executablePath, string watchdogPath,
        int excludeProcessId, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow + timeout;
        do
        {
            var owned = GetProcessesAtPath(executablePath, excludeProcessId);
            owned.AddRange(GetProcessesAtPath(watchdogPath, excludeProcessId));
            if (owned.Count == 0) return true;
            foreach (var process in owned) process.Dispose();
            Thread.Sleep(200);
        } while (DateTime.UtcNow < stopAt);
        return false;
    }

    private static bool WaitForNoProfileWebViewProcesses(string profilePath, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow + timeout;
        var queryFailureLogged = false;
        while (DateTime.UtcNow < stopAt)
        {
            var remaining = stopAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            var query = Task.Run(() => TryGetProfileWebViewProcessIds(profilePath, out var processIds,
                    logQueryFailure: !queryFailureLogged)
                ? processIds
                : null);
            if (!query.Wait(remaining)) return false;
            var matches = query.Result;
            if (matches == null)
            {
                queryFailureLogged = true;
                remaining = stopAt - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                Thread.Sleep(remaining < TimeSpan.FromMilliseconds(200)
                    ? remaining
                    : TimeSpan.FromMilliseconds(200));
                continue;
            }
            if (matches.Count == 0) return true;
            remaining = stopAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            Thread.Sleep(remaining < TimeSpan.FromMilliseconds(200)
                ? remaining
                : TimeSpan.FromMilliseconds(200));
        }
        return false;
    }

    private static bool TryGetProfileWebViewProcessIds(string profilePath, out HashSet<int> processIds,
        bool logQueryFailure)
    {
        processIds = new HashSet<int>();
        object? locator = null;
        object? services = null;
        object? processSet = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return false;
            locator = Activator.CreateInstance(locatorType);
            if (locator == null) return false;

            dynamic locatorDispatch = locator;
            services = locatorDispatch.ConnectServer(".", @"root\cimv2");
            if (services == null) return false;
            dynamic servicesDispatch = services;
            processSet = servicesDispatch.ExecQuery(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'",
                "WQL", 0);
            if (processSet == null) return false;

            dynamic processSetDispatch = processSet;
            foreach (dynamic process in processSetDispatch)
            {
                object? processObject = process;
                object? propertiesObject = null;
                object? commandLinePropertyObject = null;
                object? processIdPropertyObject = null;
                try
                {
                    propertiesObject = process.Properties_;
                    if (propertiesObject == null) return false;
                    dynamic propertiesDispatch = propertiesObject;
                    commandLinePropertyObject = propertiesDispatch.Item("CommandLine");
                    if (commandLinePropertyObject == null) return false;
                    dynamic commandLineProperty = commandLinePropertyObject;
                    object? commandLineValue = commandLineProperty.Value;
                    if (commandLineValue == null || commandLineValue == DBNull.Value) return false;
                    var commandLine = Convert.ToString(commandLineValue);
                    if (string.IsNullOrWhiteSpace(commandLine)) return false;
                    if (!IsWebViewProfileCommandLineMatch(commandLine, profilePath)) continue;

                    processIdPropertyObject = propertiesDispatch.Item("ProcessId");
                    if (processIdPropertyObject == null) return false;
                    dynamic processIdProperty = processIdPropertyObject;
                    object? processIdValue = processIdProperty.Value;
                    var processId = Convert.ToInt32(processIdValue);
                    if (processId <= 0) return false;
                    processIds.Add(processId);
                }
                finally
                {
                    ReleaseComObject(processIdPropertyObject);
                    ReleaseComObject(commandLinePropertyObject);
                    ReleaseComObject(propertiesObject);
                    ReleaseComObject(processObject);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            processIds.Clear();
            if (logQueryFailure)
                CrashReporter.Write(new InvalidOperationException(
                    $"WMI WebView profile 查询失败：{ex.GetType().FullName}，HRESULT 0x{ex.HResult:X8}"),
                    "prepare-uninstall-webview-profile-query-failed");
            return false;
        }
        finally
        {
            ReleaseComObject(processSet);
            ReleaseComObject(services);
            ReleaseComObject(locator);
        }
    }

    internal static bool IsWebViewProfileCommandLineMatch(string? commandLine, string profilePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(profilePath)) return false;
        string expectedPath;
        try { expectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilePath)); }
        catch { return false; }

        var arguments = CommandLineToArgvW(commandLine, out var argumentCount);
        if (arguments == IntPtr.Zero || argumentCount <= 0) return false;
        try
        {
            string? requestedPath = null;
            for (var index = 0; index < argumentCount; index++)
            {
                var argumentPointer = Marshal.ReadIntPtr(arguments, index * IntPtr.Size);
                var argument = Marshal.PtrToStringUni(argumentPointer);
                if (argument == null) continue;

                const string switchName = "--user-data-dir";
                if (argument.StartsWith(switchName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    requestedPath = argument[(switchName.Length + 1)..];
                }
                else if (string.Equals(argument, switchName, StringComparison.OrdinalIgnoreCase)
                    && index + 1 < argumentCount)
                {
                    var valuePointer = Marshal.ReadIntPtr(arguments, (index + 1) * IntPtr.Size);
                    requestedPath = Marshal.PtrToStringUni(valuePointer);
                    index++;
                }

                if (string.IsNullOrWhiteSpace(requestedPath)) continue;
                requestedPath = requestedPath.Trim().Trim('"');
                try
                {
                    var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
                    var childPrefix = Path.EndsInDirectorySeparator(expectedPath)
                        ? expectedPath
                        : expectedPath + Path.DirectorySeparatorChar;
                    if (string.Equals(normalizedPath, expectedPath, StringComparison.OrdinalIgnoreCase)
                        || normalizedPath.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
                requestedPath = null;
            }
            return false;
        }
        finally
        {
            LocalFree(arguments);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        try
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        catch { }
    }

    private static bool TryFindMainWindow(int processId, out IntPtr mainWindow)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var ownerProcessId);
            if (ownerProcessId == processId) windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        foreach (var hwnd in windows)
        {
            if (IsFocusDeskMainWindow(hwnd))
            {
                mainWindow = hwnd;
                return true;
            }
        }
        mainWindow = IntPtr.Zero;
        return false;
    }

    private static bool TryFindAnyTopLevelWindow(int processId, out IntPtr window)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var ownerProcessId);
            if (ownerProcessId != processId) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        window = found;
        return found != IntPtr.Zero;
    }

    private static bool IsFocusDeskMainWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var sent = SendMessageTimeout(hwnd, focus_desktop.MainWindow.WmQueryPrepareUninstall,
            UIntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out var response);
        return sent != IntPtr.Zero && response == new IntPtr(1);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, UIntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private void WireGlobalExceptionHandlers()
    {
        // 三层接线（Umbra 模式）：所有未处理异常都汇到 HandleFatal
        DispatcherUnhandledException += (_, args) =>
        {
            HandleFatal(args.Exception, "dispatcher");
            args.Handled = true;
            Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            HandleFatal(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString() ?? "?"), "appdomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            if (HandleFatal(args.Exception, "taskscheduler"))
                args.SetObserved();
            else
                Environment.Exit(1); // Dispatcher is gone; let the watchdog restore the dirty session marker.
        };
    }

    /// <summary>任何致命异常：写崩溃日志 + 尽力恢复系统状态。恢复本身绝不允许再抛出。</summary>
    private bool HandleFatal(Exception ex, string source)
    {
        try { CrashReporter.Write(ex, source); } catch { }
        try
        {
            var dispatcher = Dispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return false;

            if (dispatcher.CheckAccess())
            {
                try { _focus?.Recover(); } catch { }
                return true;
            }

            var operation = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                try { _focus?.Recover(); } catch { }
            }));
            return operation.Status != DispatcherOperationStatus.Aborted;
        }
        catch { return false; }
    }

    internal static void SmokeLog(string message)
    {
        try
        {
            File.AppendAllText(Paths.SmokeLog, $"{DateTime.Now:O} {message}\n");
        }
        catch { }
    }
}

/// <summary>命令行参数。</summary>
public sealed record AppOptions(bool Dev, bool Restore, bool Smoke, bool Preview)
{
    public bool DockAppBar { get; init; }
    public bool DockSelfTest { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var dev = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);
        return new AppOptions(
            dev,
            args.Contains("--restore", StringComparer.OrdinalIgnoreCase),
            args.Contains("--smoke", StringComparer.OrdinalIgnoreCase),
            args.Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            DockAppBar = dev && args.Contains("--dock-appbar", StringComparer.OrdinalIgnoreCase),
            DockSelfTest = args.Contains("--dockselftest", StringComparer.OrdinalIgnoreCase),
        };
    }
}
