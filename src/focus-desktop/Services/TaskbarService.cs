using System.Runtime.InteropServices;

using System.IO;
namespace focus_desktop.Services;

/// <summary>
/// 任务栏隐藏/显示。原理：窗口盖满主屏已让任务栏不可达，这里再显式隐藏
/// （SetWindowPos SWP_HIDEWINDOW），双保险。恢复 = SHOWWINDOW + 可见性校验，
/// 校验失败则重启 explorer 兜底（explorer 重启会重建任务栏）。
/// </summary>
public static class TaskbarService
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

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

    /// <summary>恢复任务栏显示。返回是否确认可见。</summary>
    public static bool Show()
    {
        foreach (var cls in TrayWindowClasses)
        {
            var hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }

        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary == IntPtr.Zero || IsWindowVisible(primary))
        {
            return true; // 已可见或任务栏不存在（罕见）——都算不需要兜底
        }

        // 兜底：explorer 状态异常时重启它，Windows 会重建任务栏
        return RestartExplorer();
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
            var hwnd = FindWindow("Shell_TrayWnd", null);
            return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
        }
        catch
        {
            return false;
        }
    }
}
