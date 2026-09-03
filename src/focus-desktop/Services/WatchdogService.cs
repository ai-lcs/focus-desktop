using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace focus_desktop.Services;

/// <summary>
/// 看门狗（Umbra watchdog 模式的轻量移植）：
/// Enter() 时通过同一二进制的硬链接名启动 focus-desktop-watchdog.exe --watchdog &lt;pid&gt;，
/// 它每 2 秒检查主进程存活 + 脏标志：
///   - 主进程死了且 focus_mode_active=true，立即恢复任务栏；确认恢复后清标志（覆盖 taskkill /f、
///     崩溃且异常处理器没跑成等一切"进程消失"场景），然后自行退出
///   - 脏标志=false（正常退出已恢复），自行退出
/// 主进程 Exit()/Recover() 时主动杀掉 watchdog。
/// 不是常驻 daemon：只在 focus session 期间存在，与主进程同生命周期。
/// watchdog 自己被杀/失败，退化到原有恢复链（启动自愈 + --restore + explorer 兜底）。
/// </summary>
public static class WatchdogService
{
    public const string ProcessName = "focus-desktop-watchdog";

    private static Process? _watchdog;
    private static readonly string WatchdogPath = Path.Combine(AppContext.BaseDirectory, $"{ProcessName}.exe");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    public static void Launch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var parentIdentity = "";
            try
            {
                using var current = Process.GetCurrentProcess();
                parentIdentity = $" {current.StartTime.ToUniversalTime().Ticks}";
            }
            catch { }
            var watchdogExe = PrepareWatchdogExecutable(exe);
            var psi = new ProcessStartInfo
            {
                FileName = watchdogExe,
                Arguments = $"--watchdog {Environment.ProcessId}{parentIdentity}",
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
        var watchdog = _watchdog;
        _watchdog = null;
        try
        {
            if (watchdog != null && !watchdog.HasExited)
            {
                watchdog.Kill();
                watchdog.WaitForExit(2000);
            }
        }
        catch { }
        finally { watchdog?.Dispose(); }

        TryDeleteWatchdogLink();
    }

    /// <summary>
    /// 同一 NTFS 文件使用独立镜像名启动：不复制大文件，也避免按名称结束主程序时误杀看门狗。
    /// 硬链接不可用（只读目录/非 NTFS）时回退原路径，保留旧恢复能力。
    /// </summary>
    private static string PrepareWatchdogExecutable(string exe)
    {
        try
        {
            TryDeleteWatchdogLink();
            if (CreateHardLink(WatchdogPath, exe, IntPtr.Zero))
                return WatchdogPath;

            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建独立看门狗硬链接");
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex, "watchdog-alias-failed");
            return exe;
        }
    }

    private static void TryDeleteWatchdogLink()
    {
        try
        {
            if (File.Exists(WatchdogPath)) File.Delete(WatchdogPath);
        }
        catch { }
    }

    /// <summary>--watchdog 参数的进程侧主循环。返回进程退出码。</summary>
    public static int RunLoop(int parentPid, long? expectedStartTicks = null)
    {
        try
        {
            for (; ; )
            {
                Thread.Sleep(2000);
                // GetProcessById 在进程不存在时抛 ArgumentException——用 try 判存活
                bool parentAlive;
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    parentAlive = !parent.HasExited;
                    if (parentAlive && expectedStartTicks.HasValue)
                    {
                        try
                        {
                            parentAlive = parent.StartTime.ToUniversalTime().Ticks == expectedStartTicks.Value;
                        }
                        catch
                        {
                            // 无法核对身份时宁可继续等待，避免误把新进程当成旧宿主。
                        }
                    }
                }
                catch (ArgumentException) { parentAlive = false; }
                catch (InvalidOperationException) { parentAlive = false; }
                var flag = RecoveryService.WasActiveLastTime();

                if (!parentAlive)
                {
                    // 主进程消失：脏标志还在，我们是最后一个能恢复系统的
                    if (flag)
                    {
                        var restored = false;
                        try { restored = TaskbarService.Show(); } catch { }
                        if (restored)
                        {
                            try { RecoveryService.MarkClean(); } catch { }
                        }
                        try
                        {
                            CrashReporter.Write(
                                new InvalidOperationException(
                                    restored
                                        ? $"主进程(pid={parentPid})非正常退出，watchdog 已恢复任务栏并清除脏标志"
                                        : $"主进程(pid={parentPid})非正常退出，watchdog 未确认任务栏恢复，保留脏标志"),
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
