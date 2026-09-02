using System.Runtime.InteropServices;

using System.IO;
namespace focus_desktop.Services;

/// <summary>
/// 任务栏隐藏/显示。原理：窗口盖满主屏已让任务栏不可达，这里再显式隐藏
/// （SetWindowPos SWP_HIDEWINDOW），双保险。
/// 恢复（2026-08-30 用户实测事故强化）：SWP_SHOWWINDOW 后必须用【窗口矩形】验证，
/// 不能信 IsWindowVisible——Win11 26200 上它会说谎（visible=true 但任务栏实际不渲染，
/// 用户退出后任务栏消失的事故根因）。验证失败→重试（ShowWindow 双保险）→explorer 重启兜底。
/// </summary>
public static class TaskbarService
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_SHOW = 5;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static readonly string[] TrayWindowClasses =
    {
        "Shell_TrayWnd",          // 主任务栏
        "Shell_SecondaryTrayWnd"  // 副屏任务栏（有的机器上存在；找不到就跳过）
    };

    public static void Hide()
    {
        foreach (var cls in TrayWindowClasses)
        {
            var hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            }
        }
    }

    /// <summary>
    /// 恢复任务栏显示（强化版）。返回是否确认真实可见（矩形验证，非 IsWindowVisible）。
    /// 流程：SHOWWINDOW → 等 150ms → 矩形验证 → 失败重试（+ShowWindow）→ 仍失败重启 explorer。
    /// </summary>
    public static bool Show()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var cls in TrayWindowClasses)
            {
                var hwnd = FindWindow(cls, null);
                if (hwnd != IntPtr.Zero)
                {
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    // 第二种原语双保险（部分 Win11 版本只认其一）
                    ShowWindow(hwnd, SW_SHOW);
                    // z 序复位（2026-09-02 用户实测事故第三层根因）：Hide/Show 循环或 explorer 异常
                    // 后任务栏可能 visible=True 但被压到普通窗口之下（IsWindowVisible 全绿、屏幕上没有）。
                    // HWND_TOPMOST→HWND_NOTOPMOST 循环把它提回正常 z 序顶端（置顶一瞬即降回，
                    // 不会真常驻 topmost）。实测修复此形态。
                    SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }

            // 等系统应用（Win11 任务栏由 explorer 异步重绘）
            Thread.Sleep(attempt == 0 ? 150 : 250);

            if (TaskbarLooksOnScreen()) return true;
        }

        // 兜底：explorer 状态异常时重启它，Windows 会重建任务栏
        return RestartExplorer();
    }

    /// <summary>矩形级验证：可见 + 有真实高度 + 贴屏幕边缘（IsWindowVisible 单独不可信）。
    /// 容忍：顶部停靠、左右竖向停靠、自动隐藏（细条）。无法判定时不触发 explorer 重启。</summary>
    private static bool TaskbarLooksOnScreen()
    {
        try
        {
            var hwnd = FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero) return false;
            if (!IsWindowVisible(hwnd)) return false;
            if (!GetWindowRect(hwnd, out var r)) return false;

            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            var sw = GetSystemMetrics(SM_CXSCREEN);
            var sh = GetSystemMetrics(SM_CYSCREEN);
            if (sw <= 0 || sh <= 0) return true; // 拿不到屏幕尺寸：宁可放过，不重启 explorer

            // 自动隐藏任务栏：可见但贴边细条
            if (h > 0 && h < 20 && w > sw / 2) return true;

            // 横向任务栏（底部或顶部停靠）
            var bottomOk = r.Bottom >= sh - 4;
            var topOk = r.Top <= 4;
            if (w > sw / 2 && h >= 20 && (bottomOk || topOk)) return true;

            // 竖向任务栏（左/右停靠，罕见）
            if (h > sh / 2 && w >= 20 && (r.Left <= 4 || r.Right >= sw - 4)) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool RestartExplorer()
    {
        try
        {
            var kill = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/f /im explorer.exe",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            kill?.WaitForExit(5000);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            });

            Thread.Sleep(2500); // 等 explorer 起来重建任务栏
            return TaskbarLooksOnScreen();
        }
        catch
        {
            return false;
        }
    }
}
