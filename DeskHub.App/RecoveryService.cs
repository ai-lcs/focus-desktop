using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace FocusDesktop;

/// <summary>
/// 专注模式生命周期 + 恢复协议（spec §4）。
///
/// 协议核心：脏标志先写后清 + 恢复代码只有一份（ExitFocusMode）。
///   EnterFocusMode: 写 focus_state.json{active:true} → 隐任务栏 → 装钩子
///   ExitFocusMode:  卸钩子 → 显任务栏 → 写 {active:false}
/// 三层异常接线和 --restore 都汇入同一条恢复路径。
/// </summary>
public sealed class RecoveryService : IDisposable
{
    public record FocusState(bool active, string enteredAt);

    private static string StatePath => AppPaths.FocusStateFile;

    // ---- 脏标志 ----

    private static void WriteState(bool active) => AtomicFile.WriteAllText(StatePath,
        JsonSerializer.Serialize(new FocusState(active,
            DateTimeOffset.Now.ToString("O"))));

    /// <summary>启动自检：上次是否非正常退出（残留 active=true）。</summary>
    public static bool WasUncleanShutdown()
    {
        try
        {
            if (!File.Exists(StatePath)) return false;
            var st = JsonSerializer.Deserialize<FocusState>(File.ReadAllText(StatePath));
            return st?.active == true;
        }
        catch { return false; }
    }

    /// <summary>--restore 独立恢复入口：不进 UI，修完就走。</summary>
    public static void RunStandaloneRestore()
    {
        TaskbarService.Show();
        if (WasUncleanShutdown())
        {
            WriteState(false);
            CrashReporter.Note("--restore: cleaned dirty flag");
        }
        CrashReporter.Note("--restore: taskbar shown");
    }

    // ---- 生命周期编排 ----

    private readonly KeyboardGuardService _keyboard = new();
    private bool _inFocus;

    public bool IsInFocusMode => _inFocus;

    public void EnterFocusMode()
    {
        if (_inFocus) return;
        WriteState(true);                 // ① 先写脏标志
        TaskbarService.Hide();            // ② 隐任务栏
        _keyboard.Install();              // ③ 装钩子
        _inFocus = true;
        CrashReporter.Note("EnterFocusMode ok");
    }

    public void ExitFocusMode()
    {
        if (!_inFocus) return;
        _keyboard.Uninstall();            // ① 卸钩子（先放键盘，最后恢复任务栏）
        TaskbarService.Show();            // ② 显任务栏
        WriteState(false);                // ③ 清脏标志
        _inFocus = false;
        CrashReporter.Note("ExitFocusMode ok");
    }

    /// <summary>崩溃兜底：同步恢复 + 写崩溃日志 + 立即退出进程。</summary>
    public static void FailSafeShutdown(string reason)
    {
        try
        {
            CrashReporter.Write(new Exception(reason), "FailSafe");
            TaskbarService.Show();
            WriteState(false);
        }
        catch { }
        try { Environment.FailFast(reason); }
        catch { Environment.Exit(13); }
    }

    public void Dispose() => _keyboard.Dispose();
}

/// <summary>App 层包装（静态可达）。</summary>
public static class FailSafe
{
    public static void ShutdownWithRecovery(string reason) =>
        RecoveryService.FailSafeShutdown(reason);
}
