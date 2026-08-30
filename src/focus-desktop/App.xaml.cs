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

        // --watchdog <pid>：看门狗伴生进程模式（无 UI，Run到主进程消失）
        if (e.Args.Length >= 2 && e.Args[0] == "--watchdog" && int.TryParse(e.Args[1], out var parentPid))
        {
            Environment.Exit(WatchdogService.RunLoop(parentPid));
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

        // --restore：纯恢复模式。不进 UI，恢复完就退。
        if (_options.Restore)
        {
            TaskbarService.Show();
            RecoveryService.MarkClean();
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

        // 配置：首次运行生成默认 config.json（白名单/退出语/学习目录，Step 3+ 使用）
        var settings = AppSettings.LoadOrDefault();
        settings.Save();

        var main = new MainWindow(_options, _focus);
        MainWindow = main;

        if (_options.Smoke)
        {
            SmokeLog("startup: smoke mode (no focus lock)");
            // 自动退出 timer 在 MainWindow.InitAsync 里（15 秒，含 Web 层初始化验证）
        }

        main.Show();

        // 锁定策略：--smoke 永不锁；--preview 预览模式（不锁+普通窗口+直接退出，给用户调配置用）；
        // 首次运行（无 setup_done）进 Setup 模式（不锁，自由登录）；此后每次启动直接锁定
        if (!_options.Dev && !_options.Smoke && !_options.Preview && !FirstRunSetup.IsSetupComplete())
        {
            FirstRunSetup.EnterSetupMode();
            App.SmokeLog("first-run setup mode (no lock)");
        }
        else if (!_options.Dev && !_options.Smoke && !_options.Preview)
        {
            _focus.Enter(); // 真实模式：进锁定
        }
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
