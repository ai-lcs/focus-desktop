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
            SmokeLog("startup: window created, smoke mode (no focus lock)");
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            t.Tick += (_, _) =>
            {
                t.Stop();
                SmokeLog("auto-shutdown timer fired");
                Shutdown();
            };
            t.Start();
        }

        main.Show();

        if (!_options.Dev && !_options.Smoke)
        {
            _focus.Enter(); // 真实模式：进锁定。dev/smoke 不锁。
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 正常退出路径：无论从哪里触发 Shutdown，都走同一条恢复逻辑
        try { _focus?.ExitIfActive(); }
        catch (Exception ex) { CrashReporter.Write(ex, "on-exit"); }
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
public sealed record AppOptions(bool Dev, bool Restore, bool Smoke)
{
    public static AppOptions Parse(string[] args) =>
        new(args.Contains("--dev", StringComparer.OrdinalIgnoreCase),
            args.Contains("--restore", StringComparer.OrdinalIgnoreCase),
            args.Contains("--smoke", StringComparer.OrdinalIgnoreCase));
}
