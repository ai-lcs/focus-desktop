using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using focus_desktop.Services;

namespace focus_desktop;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private AppOptions _options = null!;
    private FocusModeService _focus = null!;

    /// <summary>全局焦点模式服务（异常兜底和主窗口都要访问）。</summary>
    internal FocusModeService Focus => _focus;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _options = AppOptions.Parse(e.Args);
        WireGlobalExceptionHandlers();

        // 卸载器调用：先恢复 Windows 状态，再结束仍在运行的主程序/看门狗。
        // 必须位于单实例判断之前，否则已有实例会让清理入口直接退出。
        if (e.Args.Contains("--prepare-uninstall"))
        {
            try { TaskbarService.Show(); } catch { }
            try { RecoveryService.MarkClean(); } catch { }
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("focus-desktop"))
            {
                try
                {
                    if (process.Id != Environment.ProcessId)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                finally { process.Dispose(); }
            }
            Environment.Exit(0);
            return;
        }

        // --watchdog <pid> [startTicks]：看门狗伴生进程模式（无 UI，Run到主进程消失）
        if (e.Args.Length >= 2 && e.Args[0] == "--watchdog" && int.TryParse(e.Args[1], out var parentPid))
        {
            long? parentStartTicks = null;
            if (e.Args.Length >= 3 && long.TryParse(e.Args[2], out var parsedStartTicks))
                parentStartTicks = parsedStartTicks;
            Environment.Exit(WatchdogService.RunLoop(parentPid, parentStartTicks));
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
            var restored = TaskbarService.Show();
            if (restored) RecoveryService.MarkClean();
            MessageBox.Show("已尝试恢复 Windows 状态（任务栏显示 / 钩子清理）。", "focus-desktop 恢复",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 单实例：防止双击两次造成双重钩子/双重隐藏的混乱状态
        _singleInstanceMutex = new Mutex(true, "focus-desktop-single-instance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("focus-desktop 已在运行。", "focus-desktop", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _focus = new FocusModeService();

        // 自愈：上次非正常退出（脏标志残留）→ 先恢复系统状态再继续启动
        if (RecoveryService.WasActiveLastTime())
        {
            CrashReporter.Write(new InvalidOperationException("上次会话残留 focus_mode_active=true，已执行自愈恢复"),
                "startup-self-heal");
            _focus.Recover();
        }

        // 配置：新安装写入 configured:false，避免默认白名单被误判为 legacy。
        // 损坏配置先隔离原文件；即使隔离失败也不覆盖它，当前启动仍按未配置进入向导。
        var settings = AppSettings.LoadOrDefault(out var configStatus);
        if (configStatus == AppSettings.ConfigLoadStatus.Corrupt)
        {
            var backup = AppSettings.QuarantineCorruptConfig();
            if (backup != null)
            {
                settings.Configured = false;
                settings.Save();
                SmokeLog($"config corrupt; quarantined to {backup}");
            }
            else
            {
                SmokeLog("config corrupt; quarantine failed; entering setup without overwrite");
            }
        }
        else if (configStatus == AppSettings.ConfigLoadStatus.Missing)
        {
            settings.Configured = false;
            settings.Save();
        }

        var main = new MainWindow(_options, _focus);
        MainWindow = main;

        if (_options.Smoke)
        {
            SmokeLog("startup: smoke mode (no focus lock)");
            // 自动退出 timer 在 MainWindow.InitAsync 里（15 秒，含 Web 层初始化验证）
        }

        main.Show();

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
                App.SmokeLog("first-run setup wizard mode (no lock)");
            }
            else if (!legacy && !FirstRunSetup.IsSetupComplete())
            {
                main.ShowLoginHint();
                App.SmokeLog("configured setup pending; login guidance mode (no lock)");
            }
            else
            {
                _focus.Enter(); // 真实模式：进锁定
            }
        }
    }

    /// <summary>Public v1 首次配置向导：独立普通窗口（盖在 MainWindow 上；不保存、不进锁定）。</summary>
    private void ShowSetupWizard(AppSettings draft)
    {
        var wizard = new SetupWizard(draft, (MainWindow)MainWindow);
        wizard.Completed += OnSetupWizardCompleted;
        wizard.Show();
    }

    /// <summary>向导完成（config 已原子写入）→ 进入下一步：登录引导横幅。</summary>
    private void OnSetupWizardCompleted(AppSettings finalConfig)
    {
        if (MainWindow is MainWindow main)
        {
            try
            {
                main.NotifyConfigCommitted(); // F1：首配轮预热延迟到此刻——最终配置已落盘，新 WebView 捕获新策略
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
        // 正常退出路径：无论从哪里触发 Shutdown，都走同一条恢复逻辑
        try { _focus?.ExitIfActive(); }
        catch (Exception ex) { CrashReporter.Write(ex, "on-exit"); }

        // 收尾兜底（2026-08-30 事故强化）：Exit() 里的 Show() 可能被「全屏窗口还在」
        // 吞掉（Win11 任务栏延迟应用）；此时所有窗口已关，再验证并补一次，确保任务栏可见
        try { TaskbarService.Show(); }
        catch (Exception ex) { CrashReporter.Write(ex, "on-exit-taskbar"); }

        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }

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
            args.SetObserved();
            HandleFatal(args.Exception, "taskscheduler");
        };
    }

    /// <summary>任何致命异常：写崩溃日志 + 尽力恢复系统状态。恢复本身绝不允许再抛出。</summary>
    private void HandleFatal(Exception ex, string source)
    {
        try { CrashReporter.Write(ex, source); } catch { }
        try { _focus?.Recover(); } catch { }
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
    public static AppOptions Parse(string[] args) =>
        new(args.Contains("--dev", StringComparer.OrdinalIgnoreCase),
            args.Contains("--restore", StringComparer.OrdinalIgnoreCase),
            args.Contains("--smoke", StringComparer.OrdinalIgnoreCase),
            args.Contains("--preview", StringComparer.OrdinalIgnoreCase));
}
