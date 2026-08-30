// Win32Probe.cs — verify-step12 用的 Win32 探针（预编译 DLL，根治 PS5.1 Add-Type 引用集漂移）
// C#5 兼容（csc v4.0.30319 编译）
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Win32Probe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static string GetTitle(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        return sb.ToString();
    }

    public static string ProcessNameOf(int pid)
    {
        try { var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName + ":" + pid; }
        catch { return "dead:" + pid; }
    }

    public static string FindFocusWindow()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "none";
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        return ProcessNameOf((int)pid);
    }

    public static string ForegroundProcessName()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "none";
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return "dead"; }
    }

    public static IntPtr FindOtherWindowOfProcess(int pid, IntPtr exclude)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindowsProc cb = delegate(IntPtr h, IntPtr lParam)
        {
            uint wp;
            GetWindowThreadProcessId(h, out wp);
            if (wp == (uint)pid && h != exclude && IsWindowVisible(h))
            {
                string t = GetTitle(h);
                if (!string.IsNullOrEmpty(t)) { found = h; return false; }
            }
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        return found;
    }

    public static bool TaskbarHidden()
    {
        var h = FindWindow("Shell_TrayWnd", null);
        return h == IntPtr.Zero || !IsWindowVisible(h);
    }

    public static bool TaskbarShow()
    {
        var h = FindWindow("Shell_TrayWnd", null);
        return h != IntPtr.Zero && IsWindowVisible(h);
    }

    public static int GetSystemMetricsSafe(int idx) { return GetSystemMetrics(idx); }

    public static string WindowStateOf(string proc)
    {
        // "procname:pid" → "SW_MAXIMIZED / normal / minimized"
        int idx = proc.LastIndexOf(':');
        if (idx < 0) return "unknown";
        int pid;
        if (!int.TryParse(proc.Substring(idx + 1), out pid)) return "unknown";
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            if (p == null) return "dead";
            // IsIconic = minimized
            IntPtr h = p.MainWindowHandle;
            if (h == IntPtr.Zero) return "nohwnd";
            return WindowStateOfHwnd(h);
        }
        catch { return "dead"; }
    }

    private static string WindowStateOfHwnd(IntPtr h)
    {
        const int SM_CYSCREEN = 1, SM_CXSCREEN = 0;
        RECT r;
        GetWindowRect(h, out r);
        var sw = GetSystemMetrics(SM_CXSCREEN);
        var sh = GetSystemMetrics(SM_CYSCREEN);
        if (r.Right - r.Left >= sw - 2 && r.Bottom - r.Top >= sh - 2) return "maximized";
        if (!IsWindowVisible(h)) return "hidden";
        return "normal";
    }

    public static bool StartMenuVisible()
    {
        // 开始菜单（Win11: "开始" 窗口 / Win10: Windows.UI.Core.CoreWindow）
        var h1 = FindWindow("Windows.UI.Core.CoreWindow", "开始");
        var h2 = FindWindow(null, "开始");
        if (h1 != IntPtr.Zero && IsWindowVisible(h1)) return true;
        if (h2 != IntPtr.Zero && IsWindowVisible(h2)) return true;
        return false;
    }

    public static void mouse(uint flags, uint dx, uint dy)
    {
        mouse_event(flags, dx, dy, 0, UIntPtr.Zero);
    }

    public static void SendWinKey()
    {
        const byte VK_LWIN = 0x5B;
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, 2 /*KEYEVENTF_KEYUP*/, UIntPtr.Zero);
    }

    public static IntPtr SendMessageSafe(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp)
    {
        IntPtr result;
        SendMessageTimeout(hWnd, msg, wp, lp, 2 /*SMTO_ABORTIFHUNG*/, 1500, out result);
        return result;
    }
}
