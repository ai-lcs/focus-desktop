using System.Runtime.InteropServices;

namespace focus_desktop.Services;

/// <summary>Temporarily fits selected root windows; restores their original placement.</summary>
internal sealed class ExternalWindowLayout : IDisposable
{
    internal string LastFailure { get; private set; } = "";
    private bool Fail(string reason) { LastFailure = reason; return false; }
    private readonly string _marker = "FocusDesk.DockLayout." + Guid.NewGuid().ToString("N");
    private readonly Dictionary<IntPtr, Placement> _original = new();
    private readonly HashSet<IntPtr> _fitted = new();
    private readonly object _captureGate = new();
    private bool _disposed;

    internal bool Capture(IntPtr window)
    {
        lock (_captureGate)
        {
            if (_disposed) return false;
            if (_original.ContainsKey(window) && GetProp(window, _marker) == new IntPtr(1)) return true;
            _original.Remove(window);
            _fitted.Remove(window);
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(window, ref placement) || !SetProp(window, _marker, new IntPtr(1))) return false;
            _original.Add(window, placement);
            return true;
        }
    }

    internal async Task<bool> FitAsync(IntPtr window, DesktopRect viewport)
    {
        if (GetWindow(window, 4) != IntPtr.Zero) return true; // Preserve owned dialogs.
        if (!Capture(window)) return Fail("placement unavailable");
        if (_fitted.Add(window))
        {
            PostMessage(window, 0x001A, new IntPtr(0x002F), IntPtr.Zero);
            // Custom-framed applications may maintain a maximized state without
            // reporting IsZoomed. Request their normal restore command as well.
            PostMessage(window, 0x0112, new IntPtr(0xF120), IntPtr.Zero);
            await Task.Delay(32);
        }
        if (GetProp(window, _marker) != new IntPtr(1)) return false;
        if (IsZoomed(window) || IsIconic(window))
        {
            ShowWindowAsync(window, 9);
            for (var attempt = 0; attempt < 30 && (IsZoomed(window) || IsIconic(window)); attempt++)
                await Task.Delay(16);
            if (GetProp(window, _marker) != new IntPtr(1) || IsZoomed(window) || IsIconic(window)) return Fail("restore not acknowledged");
        }
        if (!GetWindowRect(window, out var rect)) return false;
        var frame = rect;
        if (DwmGetWindowAttribute(window, 9, out Rect measured, Marshal.SizeOf<Rect>()) == 0) frame = measured;
        var left = viewport.Left - (frame.Left - rect.Left);
        var top = viewport.Top - (frame.Top - rect.Top);
        var right = viewport.Right + (rect.Right - frame.Right);
        var bottom = viewport.Bottom + (rect.Bottom - frame.Bottom);
        if (!SetWindowPos(window, IntPtr.Zero, left, top, right - left, bottom - top, 0x0010 | 0x0004 | 0x0200 | 0x4000)) return Fail("position denied");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (GetProp(window, _marker) != new IntPtr(1)) return false;
            if (DwmGetWindowAttribute(window, 9, out measured, Marshal.SizeOf<Rect>()) != 0
                && !GetWindowRect(window, out measured)) return false;
            if (Math.Abs(measured.Left - viewport.Left) <= 1 && Math.Abs(measured.Top - viewport.Top) <= 1
                && Math.Abs(measured.Right - viewport.Right) <= 1 && Math.Abs(measured.Bottom - viewport.Bottom) <= 1) return true;
            await Task.Delay(16);
        }
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor);
        return Fail($"expected {viewport.Left},{viewport.Top},{viewport.Right},{viewport.Bottom}; actual {measured.Left},{measured.Top},{measured.Right},{measured.Bottom}; workBottom={monitor.Work.Bottom}; window {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
    }

    public void Dispose()
    {
        lock (_captureGate)
        {
            _disposed = true;
            foreach (var (window, saved) in _original)
            {
                // Properties disappear with the HWND: do not restore a recycled handle.
                if (GetProp(window, _marker) != new IntPtr(1)) continue;
                var placement = saved;
                placement.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT: do not wait on a foreign UI thread.
                if (_fitted.Contains(window) && !SetWindowPlacement(window, ref placement))
                    CrashReporter.Write(new InvalidOperationException("外部窗口原位置未能恢复。"), "external-window-layout");
                RemoveProp(window, _marker);
            }
            _original.Clear();
            _fitted.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public int Length, Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr hwnd, string name);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
