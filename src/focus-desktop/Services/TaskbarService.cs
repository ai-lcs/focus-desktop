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
    private const long WsExLayered = 0x00080000;
    private const string RevealSnapshot = "FocusDesk.Dock.TaskbarReveal.v1";

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr hwnd, string name);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static readonly string[] TrayWindowClasses =
    {
        "Shell_TrayWnd",          // 主任务栏
        "Shell_SecondaryTrayWnd"  // 副屏任务栏（有的机器上存在；找不到就跳过）
    };

    internal static int ReadShellState()
    {
        var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>(), Window = FindWindow("Shell_TrayWnd", null) };
        if (data.Window == IntPtr.Zero) throw new InvalidOperationException("未找到系统任务栏。");
        return (int)SHAppBarMessage(4, ref data).ToUInt64();
    }

    private static bool SetShellState(int state)
    {
        var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>(), Window = FindWindow("Shell_TrayWnd", null), Parameter = new IntPtr(state) };
        SHAppBarMessage(10, ref data);
        return ReadShellState() == state;
    }

    [StructLayout(LayoutKind.Sequential)] private struct AppBarData
    {
        public int Size;
        public IntPtr Window;
        public uint Callback, Edge;
        public RECT Rectangle;
        public IntPtr Parameter;
    }
    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

    public static bool Hide(bool releaseReservation = false)
    {
        if (!DesktopSessionCoordinator.IsHeldByCurrentThread) return false;
        // Explorer can show an auto-hidden tray again when the pointer touches
        // the edge. Alpha zero keeps that window invisible and hit-test transparent.
        // Store its original appearance on the HWND so the watchdog can restore it;
        // a recreated Explorer window cannot inherit an old window's snapshot.
        foreach (var cls in TrayWindowClasses)
        {
            var hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero && !SuppressReveal(hwnd)) return false;
        }
        if (releaseReservation && !SetShellState(ReadShellState() | 1)) return false;

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

    private static bool SuppressReveal(IntPtr hwnd)
    {
        if (GetProp(hwnd, RevealSnapshot) == IntPtr.Zero)
        {
            var layered = (GetWindowLongPtr(hwnd, -20).ToInt64() & WsExLayered) != 0;
            if (layered)
            {
                if (!GetLayeredWindowAttributes(hwnd, out var key, out var alpha, out var flags)) return false;
                if (!SetProp(hwnd, RevealSnapshot + ".key", new IntPtr(key))
                    || !SetProp(hwnd, RevealSnapshot + ".alpha", new IntPtr(alpha))
                    || !SetProp(hwnd, RevealSnapshot + ".flags", new IntPtr(flags)))
                {
                    ClearRevealSnapshot(hwnd);
                    return false;
                }
            }
            if (!SetProp(hwnd, RevealSnapshot, new IntPtr(layered ? 2 : 1)))
            {
                ClearRevealSnapshot(hwnd);
                return false;
            }
        }
        SetWindowLongPtr(hwnd, -20, new IntPtr(GetWindowLongPtr(hwnd, -20).ToInt64() | WsExLayered));
        return SetLayeredWindowAttributes(hwnd, 0, 0, 2) && IsRevealSuppressed(hwnd);
    }

    private static bool IsRevealSuppressed(IntPtr hwnd) =>
        GetProp(hwnd, RevealSnapshot).ToInt64() is 1 or 2
        && (GetWindowLongPtr(hwnd, -20).ToInt64() & WsExLayered) != 0
        && GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)
        && alpha == 0 && (flags & 2) != 0;

    internal static bool IsRevealSuppressed() => IsRevealSuppressed(FindWindow("Shell_TrayWnd", null));

    private static bool RestoreReveal(IntPtr hwnd)
    {
        var original = GetProp(hwnd, RevealSnapshot).ToInt64();
        if (original == 0) return true;
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        if (original == 1)
        {
            SetWindowLongPtr(hwnd, -20, new IntPtr(style & ~WsExLayered));
            if ((GetWindowLongPtr(hwnd, -20).ToInt64() & WsExLayered) != 0) return false;
        }
        else if (original == 2)
        {
            var key = (uint)GetProp(hwnd, RevealSnapshot + ".key").ToInt64();
            var alpha = (byte)GetProp(hwnd, RevealSnapshot + ".alpha").ToInt64();
            var flags = (uint)GetProp(hwnd, RevealSnapshot + ".flags").ToInt64();
            SetWindowLongPtr(hwnd, -20, new IntPtr(style | WsExLayered));
            if (!SetLayeredWindowAttributes(hwnd, key, alpha, flags)
                || !GetLayeredWindowAttributes(hwnd, out var actualKey, out var actualAlpha, out var actualFlags)
                || actualKey != key || actualAlpha != alpha || actualFlags != flags) return false;
        }
        else return false;
        ClearRevealSnapshot(hwnd);
        return true;
    }

    private static void ClearRevealSnapshot(IntPtr hwnd)
    {
        RemoveProp(hwnd, RevealSnapshot + ".key");
        RemoveProp(hwnd, RevealSnapshot + ".alpha");
        RemoveProp(hwnd, RevealSnapshot + ".flags");
        RemoveProp(hwnd, RevealSnapshot);
    }

    /// <summary>
    /// 恢复任务栏显示。仅会话锁持有线程可执行；重试后仍无法确认真实可见时返回 false。
    /// </summary>
    public static bool Show()
    {
        if (!DesktopSessionCoordinator.IsHeldByCurrentThread) return false;

        try
        {
            if (RecoveryService.OriginalTaskbarState() is int state && !SetShellState(state)) return false;
            foreach (var cls in TrayWindowClasses)
            {
                var hwnd = FindWindow(cls, null);
                if (hwnd != IntPtr.Zero && !RestoreReveal(hwnd)) return false;
            }
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
