using System.IO;
using System.Windows;

using System.Windows.Interop;

namespace FocusDesktop;

public partial class App : Application
{
    public static AppSettings Config { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 参数分发（Umbra --watchdog 模式的简化版）
        var args = e.Args;
        if (args.Contains("--restore"))
        {
            RecoveryService.RunStandaloneRestore();
            Shutdown();
            return;
        }

        DevMode = args.Contains("--dev");

        // ---- 异常接线（三层，全部走同一条恢复路径）----
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            FailSafe.ShutdownWithRecovery($"Dispatcher: {ex.Exception.Message}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exc)
                FailSafe.ShutdownWithRecovery($"AppDomain: {exc.Message}");
            else
                FailSafe.ShutdownWithRecovery("AppDomain: non-CLR exception");
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            ex.SetObserved();
            CrashReporter.Write(new Exception("Unobserved task exception"), "TaskScheduler");
        };

        Config = AppSettings.LoadOrDefault();
        Config.Save(); // 首次运行生成默认 config.json

        var window = new MainWindow(DevMode);
        window.Show();
        MainWindowHandle = new WindowInteropHelper(window).Handle;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 只有正常退出路径才允许走这里时不触发恢复——恢复已在 ExitFocusMode 完成
        base.OnExit(e);
    }

    public static bool DevMode { get; private set; }
    public static nint MainWindowHandle { get; private set; }
}
