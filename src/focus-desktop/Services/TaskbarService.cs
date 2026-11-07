using System.Runtime.InteropServices;

using System.IO;
namespace focus_desktop.Services;

/// <summary>
/// 任务栏隐藏/显示。原理：窗口盖满主屏已让任务栏不可达，这里再显式隐藏
/// （SetWindowPos SWP_HIDEWINDOW），双保险。
/// 恢复时保留有界原语重试和窗口矩形验证。验证失败返回 false，由调用方保留脏标记并阻止清数据。
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

    public static bool Hide()
    {
        if (!DesktopSessionCoordinator.IsHeldByCurrentThread) return false;

        var primaryFound = false;
        var succeeded = true;
        foreach (var cls in TrayWindowClasses)
        {
            var hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero)
            {
                if (cls == TrayWindowClasses[0]) primaryFound = true;
                if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW))
                    succeeded = false;
                if (IsWindowVisible(hwnd)) succeeded = false;
            }
        }

        return primaryFound && succeeded;
    }

    /// <summary>
    /// 恢复任务栏显示。仅会话锁持有线程可执行；重试后仍无法确认真实可见时返回 false。
    /// </summary>
    public static bool Show()
    {
        if (!DesktopSessionCoordinator.IsHeldByCurrentThread) return false;

        try
        {
            return TaskbarRestorePolicy.TryRestore(attempt =>
            {
                ShowTaskbarsOnce();
                Thread.Sleep(attempt == 0 ? 150 : 250);
                return TaskbarLooksOnScreen();
            });
        }
        catch
        {
            return false;
        }
    }

    private static void ShowTaskbarsOnce()
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
    }

    /// <summary>矩形级验证：可见 + 有真实高度 + 贴屏幕边缘（IsWindowVisible 单独不可信）。</summary>
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

}
