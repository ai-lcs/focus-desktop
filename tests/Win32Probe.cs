// Win32Probe.cs — verify-step12.ps1 探针（预编译 DLL，根治 PS5.1 Add-Type -ReferencedAssemblies 漂移）。
// !! 方法签名/语义必须与 git HEAD 版 here-string 逐字一致 —— 那是 13/13 全绿、实测定罪过
// 应用 bug 的版本；改语义=改判据（2026-08-31 曾因语义漂移制造 4 个误报）。
// C#5 兼容（csc v4.0.30319 编译）：不用 out var、不用 lambda 默认参数。
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Win32Probe {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr i, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    public static int GetSystemMetricsSafe(int i) { try { return GetSystemMetrics(i); } catch { return 0; } }
    public static IntPtr FindOtherWindowOfProcess(IntPtr mainHwnd, int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid == (uint)pid && h != mainHwnd && IsWindowVisible(h) && GetParent(h) == IntPtr.Zero) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // 字段名必须为 Left/Top/Right/Bottom：脚本用 $r.Right/$r.Left 等访问。
    // 若命名为 L/T/R/B，PowerShell 的 .Right 取到空 → 退出弹窗坐标算成 (0,0) → 干净退出失败
    // （2026-08-31 探针实测证实）。GetWindowRect 按 left,top,right,bottom 顺序填充，字段名不影响封送。
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static string ForegroundProcessName() {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        try {
            var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName + ":" + pid;
        } catch { return "unknown:" + pid; }
    }

    public static bool TaskbarHidden() {
        var h = FindWindow("Shell_TrayWnd", null);
        return h != IntPtr.Zero && !IsWindowVisible(h);
    }

    public static void TaskbarShow() {
        var h = FindWindow("Shell_TrayWnd", null);
        if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, 0,0,0,0, 0x0001|0x0002|0x0010|0x0040);
    }

    public static string WindowStateOf(IntPtr h) {
        if (h == IntPtr.Zero) return "notfound";
        var style = GetWindowLong(h, -16);
        bool caption = (style & 0x00C00000) != 0;
        var ex = GetWindowLong(h, -20);
        bool top = (ex & 0x8) != 0;
        var r = new RECT(); GetWindowRect(h, out r);
        return string.Format("caption={0} topmost={1} rect=({2},{3})-({4},{5})",
            caption, top, r.Left, r.Top, r.Right, r.Bottom);
    }

    public static IntPtr FindFocusWindow() {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("focus-desktop")) {
            if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
        }
        return IntPtr.Zero;
    }

    // Win11 开始菜单检测（实测有效的唯一判据）：
    // 弹出时前台窗口标题变为「开始」/「Start」
    public static bool StartMenuVisible() {
        var h = GetForegroundWindow();
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        var t = sb.ToString();
        return t == "开始" || t == "Start";
    }

    // 注入 Win 键（SendKeys 不支持 LWIN，用 keybd_event）
    public static void SendWinKey() {
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);        // VK_LWIN down
        System.Threading.Thread.Sleep(50);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);        // KEYEVENTF_KEYUP
    }
}

// 任务栏强制隐藏探针（原脚本第二个 here-string 块 TBHide，并入本 DLL 一起预编译）。
public static class TBHide {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr i, int x, int y, int cx, int cy, uint f);

    public static void Hide() {
        var h = FindWindow("Shell_TrayWnd", null);
        // SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE|SWP_NOOWNERZORDER
        if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0080);
    }
}
