using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace focus_desktop.Services;

/// <summary>
/// 看门狗（Umbra watchdog 模式的轻量移植）：
/// Enter() 时通过同一二进制的硬链接名启动 focus-desktop-watchdog.exe --watchdog &lt;pid&gt;，
/// 它每 2 秒检查主进程存活 + 脏标志：
///   - 主进程死了且会话 generation 仍匹配，取得桌面及配置目录协调权后才恢复
///   - 脏标志=false（正常退出已恢复），自行退出
/// 主进程 Exit()/Recover() 时主动杀掉 watchdog。
/// 不是常驻 daemon：只在 focus session 期间存在，与主进程同生命周期。
/// watchdog 自己失败时，恢复标记保留，由启动自愈或手动恢复接手。
/// </summary>
public static class WatchdogService
{
    public const string ProcessName = "focus-desktop-watchdog";

    private static Process? _watchdog;
    private static readonly string WatchdogPath = Path.Combine(AppContext.BaseDirectory, $"{ProcessName}.exe");
    public static string WatchdogExecutablePath => WatchdogPath;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    public static void Launch(string sessionGeneration)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || string.IsNullOrWhiteSpace(sessionGeneration)) return;
            using var current = Process.GetCurrentProcess();
            var parentStartTicks = current.StartTime.ToUniversalTime().Ticks;
            var watchdogExe = PrepareWatchdogExecutable(exe);
            var psi = new ProcessStartInfo
            {
                FileName = watchdogExe,
                Arguments = $"--watchdog {current.Id} {parentStartTicks} {sessionGeneration}",
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
    public static int RunLoop(int parentPid, long expectedStartTicks, string expectedGeneration)
    {
        try
        {
            if (parentPid <= 0 || expectedStartTicks <= 0 || string.IsNullOrWhiteSpace(expectedGeneration))
                return 1;

            for (; ; )
            {
                Thread.Sleep(2000);
                // GetProcessById 在进程不存在时抛 ArgumentException——用 try 判存活
                bool parentAlive;
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    parentAlive = !parent.HasExited;
                    if (parentAlive)
                    {
                        try
                        {
                            parentAlive = parent.StartTime.ToUniversalTime().Ticks == expectedStartTicks;
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
                    // 主进程消失：恢复前先取得两个协调锁，再逐次核对 generation 和进程身份。
                    if (flag)
                    {
                        if (!DesktopSessionCoordinator.TryAcquire(out var desktopLease, out _)) return 1;
                        using (desktopLease!)
                        {
                            if (!ProfileUsageLease.TryAcquire(Paths.DataDir, out var profileLease, out _))
                                return 1;
                            using (profileLease!)
                            {
                                var recoveryReason = "会话 generation、主进程身份或恢复状态已变化";
                                var markerCleaned = DesktopSessionRecoveryProtocol.TryRunWatchdogRecovery(
                                    () => RecoveryService.TryGetRecoverableSession(out var session, out _)
                                        ? session
                                        : null,
                                    expectedGeneration,
                                    parentPid,
                                    expectedStartTicks,
                                    RecoveryService.IsProcessIdentityExited,
                                    () =>
                                    {
                                        var restored = TaskbarService.Show();
                                        if (!restored) recoveryReason = "任务栏恢复未确认";
                                        return restored;
                                    },
                                    session => RecoveryService.TryConfirmAppBarReleased(
                                        session.ParentProcessId, session.ParentStartTicks, out recoveryReason),
                                    session =>
                                    {
                                        var clean = RecoveryService.TryMarkCleanIfCurrent(session);
                                        if (!clean) recoveryReason = "会话身份在清理恢复标记前发生变化";
                                        return clean;
                                    });
                                try
                                {
                                    CrashReporter.Write(
                                        new InvalidOperationException(markerCleaned
                                            ? $"主进程(pid={parentPid})非正常退出；watchdog 已取得会话协调权并确认恢复：{recoveryReason}"
                                            : $"主进程(pid={parentPid})非正常退出；watchdog 未确认当前会话恢复，保留恢复标记：{recoveryReason}"),
                                        markerCleaned ? "watchdog-recovered" : "watchdog-recovery-incomplete");
                                }
                                catch { }
                                return markerCleaned ? 0 : 1;
                            }
                        }
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
