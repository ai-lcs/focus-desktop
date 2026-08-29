using System.Runtime.InteropServices;

namespace FocusDesktop;

/// <summary>
/// 任务栏隐藏/恢复。SetWindowPos(Shell_TrayWnd / Shell_SecondaryTrayWnd)。
/// 单显示器版：Secondary 也一并处理，成本一行。
/// </summary>
public static class TaskbarService
{
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int HWND_BOTTOM = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static readonly string[] TrayClasses =
    {
        "Shell_TrayWnd",        // 主任务栏
        "Shell_SecondaryTrayWnd" // 副任务栏（单显示器下通常不存在）
    };

    public static void Hide()
    {
        foreach (var cls in TrayClasses)
        {
            var hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
                SetWindowPos(hwnd, (IntPtr)HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        }
    }

    public static void Show()
    {
        foreach (var cls in TrayClasses)
        {
            var hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero && !IsWindowVisible(hwnd))
                SetWindowPos(hwnd, (IntPtr)HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    /// <summary>当前任务栏是否处于隐藏状态（用于恢复自检）。</summary>
    public static bool IsHidden()
    {
        foreach (var cls in TrayClasses)
        {
            var hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero && !IsWindowVisible(hwnd))
                return true;
        }
        return false;
    }
}
