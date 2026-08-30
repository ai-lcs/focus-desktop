using System.IO;
using System.Diagnostics;

namespace focus_desktop.Services;

/// <summary>
/// 看门狗（Umbra watchdog 模式的轻量移植）：
/// Enter() 时启动伴生进程 focus-desktop.exe --watchdog &lt;pid&gt;，
/// 它每 2 秒检查主进程存活 + 脏标志：
///   - 主进程死了且 focus_mode_active=true，立即恢复任务栏 + 清标志（覆盖 taskkill /f、
///     崩溃且异常处理器没跑成等一切"进程消失"场景），然后自行退出
///   - 脏标志=false（正常退出已恢复），自行退出
/// 主进程 Exit()/Recover() 时主动杀掉 watchdog。
/// 不是常驻 daemon：只在 focus session 期间存在，与主进程同生命周期。
/// watchdog 自己被杀/失败，退化到原有恢复链（启动自愈 + --restore + explorer 兜底）。
/// </summary>
public static class WatchdogService
{
    private static Process? _watchdog;

    public static void Launch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--watchdog {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            _watchdog = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // watchdog 是增强层，启动失败不阻塞锁定（原有恢复链仍在）
            CrashReporter.Write(ex, "watchdog-launch-failed");
        }
    }

    public static void Stop()
    {
        try
        {
            if (_watchdog != null && !_watchdog.HasExited)
                _watchdog.Kill();
        }
        catch { }
        _watchdog = null;
    }

    /// <summary>--watchdog 参数的进程侧主循环。返回进程退出码。</summary>
    public static int RunLoop(int parentPid)
    {
        try
        {
            for (; ; )
            {
                Thread.Sleep(2000);
                // GetProcessById 在进程不存在时抛 ArgumentException——用 try 判存活
                bool parentAlive;
                try { parentAlive = Process.GetProcessById(parentPid) != null; }
                catch (ArgumentException) { parentAlive = false; }
                var flag = RecoveryService.WasActiveLastTime();

                if (!parentAlive)
                {
                    // 主进程消失：脏标志还在，我们是最后一个能恢复系统的
                    if (flag)
                    {
                        try { TaskbarService.Show(); } catch { }
                        try { RecoveryService.MarkClean(); } catch { }
                        try
                        {
                            CrashReporter.Write(
                                new InvalidOperationException(
                                    $"主进程(pid={parentPid})非正常退出，watchdog 已恢复任务栏并清除脏标志"),
                                "watchdog-recovered");
                        }
                        catch { }
                    }
                    return 0; // 使命完成（或无事发生），退出
                }

                if (!flag && parentAlive)
                {
                    // 正常退出路径：主进程清了标志但还没来得及杀我们（race），退场
                    return 0;
                }
                // parentAlive && flag=true：session 进行中，继续守望
            }
        }
        catch
        {
            return 1;
        }
    }
}
