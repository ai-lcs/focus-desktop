using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace focus_desktop.Services;

internal readonly record struct DesktopRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

internal readonly record struct MonitorWorkAreaSnapshot(
    string Device,
    DesktopRect MonitorBounds,
    DesktopRect WorkArea);

internal readonly record struct AppBarLayout(DesktopRect ApprovedBounds, DesktopRect WorkArea);

/// <summary>Registers the Dock as a standard left-edge Windows appbar.</summary>
internal sealed class AppBarWindowService : IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbmActivate = 0x00000006;
    private const uint AbmWindowPosChanged = 0x00000009;
    private const uint AbeLeft = 0;
    private const uint AbnStateChange = 0;
    private const uint AbnPosChanged = 1;
    private const uint AbnFullScreenApp = 3;
    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToNull = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndBottom = new(1);

    private readonly Window _window;
    private readonly bool _fullScreen;
    private readonly HwndSourceHook _hook;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private uint _callbackMessage;
    private int _desiredWidthPixels;
    private double _desiredWidthDip;
    private bool _registered;
    private bool _positioning;
    private bool _registrationLossReported;
    private bool _disposed;

    public bool IsRegistered => _registered;
    public AppBarLayout? Layout { get; private set; }

    public event Action<string>? RegistrationLost;
    public event Action<AppBarLayout?>? LayoutChanged;

    public AppBarWindowService(Window window, bool fullScreen)
    {
        _window = window;
        _fullScreen = fullScreen;
        _hook = WndProc;
    }

    public bool Register()
    {
        if (_registered) return true;
        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == IntPtr.Zero) return false;

        _callbackMessage = RegisterWindowMessage("FocusDesk.AppBar.Callback");
        if (_callbackMessage == 0) return false;
        _source = HwndSource.FromHwnd(_hwnd);
        if (_source == null) return false;
        _source.AddHook(_hook);

        _desiredWidthDip = _window.Width;
        RecalculateDesiredWidth();
        var screenWidth = GetSystemMetrics(0);
        if (screenWidth <= 0 || _desiredWidthPixels >= screenWidth)
        {
            _source.RemoveHook(_hook);
            _source = null;
            return false;
        }

        if (!TryCaptureMonitorSnapshot(_window, out var currentMonitor)
            || !RecoveryService.TryRecordAppBarCandidate(_hwnd, currentMonitor))
        {
            RemoveHook();
            return false;
        }

        var data = CreateData();
        data.CallbackMessage = _callbackMessage;
        if (SHAppBarMessage(AbmNew, ref data) == UIntPtr.Zero)
        {
            _source.RemoveHook(_hook);
            _source = null;
            return false;
        }
        _registered = true;

        if (!Reposition())
        {
            Release();
            return false;
        }

        _window.Topmost = true;
        if (!SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0,
                SwpNoActivate | SwpNoOwnerZOrder | SwpNoMove | SwpNoSize))
        {
            Release();
            return false;
        }
        return true;
    }

    public bool Release()
    {
        if (!_registered)
        {
            RemoveHook();
            return true;
        }

        var data = CreateData();
        var removed = SHAppBarMessage(AbmRemove, ref data) != UIntPtr.Zero;
        _registered = !removed;
        if (removed)
        {
            _window.Topmost = false;
            RemoveHook();
            Layout = null;
            LayoutChanged?.Invoke(null);
        }
        return removed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    internal static bool TryCaptureMonitorSnapshot(Window window, out MonitorWorkAreaSnapshot snapshot)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = hwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>(), Device = string.Empty };
        if (monitor != IntPtr.Zero && GetMonitorInfoEx(monitor, ref info)
            && !string.IsNullOrWhiteSpace(info.Device))
        {
            snapshot = new MonitorWorkAreaSnapshot(
                info.Device,
                new DesktopRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
                new DesktopRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom));
            return true;
        }

        snapshot = default;
        return false;
    }

    internal static bool IsOriginalWindowDestroyed(IntPtr hwnd, int processId, long processStartTicks)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return true;
        if (GetWindowThreadProcessId(hwnd, out var ownerProcessId) == 0) return false;
        if (ownerProcessId != (uint)processId) return true;

        try
        {
            using var owner = System.Diagnostics.Process.GetProcessById(processId);
            return owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != processStartTicks;
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }

    internal static bool WorkAreaMatches(MonitorWorkAreaSnapshot expected)
    {
        var rect = new RECT
        {
            Left = expected.MonitorBounds.Left,
            Top = expected.MonitorBounds.Top,
            Right = expected.MonitorBounds.Right,
            Bottom = expected.MonitorBounds.Bottom,
        };
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNull);
        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>(), Device = string.Empty };
        if (monitor == IntPtr.Zero || !GetMonitorInfoEx(monitor, ref info)) return false;

        var actualMonitor = new DesktopRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
        var actualWork = new DesktopRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        return string.Equals(info.Device, expected.Device, StringComparison.OrdinalIgnoreCase)
            && actualMonitor == expected.MonitorBounds
            && actualWork == expected.WorkArea;
    }

    private bool Reposition()
    {
        if (!_registered || _hwnd == IntPtr.Zero) return false;
        if (_positioning) return true;
        _positioning = true;
        try
        {
            return RepositionCore();
        }
        finally
        {
            _positioning = false;
        }
    }

    private bool RepositionCore()
    {
        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);
        if (screenWidth <= 0 || screenHeight <= 0 || _desiredWidthPixels <= 0 || _desiredWidthPixels >= screenWidth)
            return false;

        var data = CreateData();
        data.Edge = AbeLeft;
        data.Rect = new RECT { Left = 0, Top = 0, Right = _desiredWidthPixels, Bottom = screenHeight };
        if (SHAppBarMessage(AbmQueryPos, ref data) == UIntPtr.Zero) return false;

        // QueryPos may move the bar to make room for existing appbars. Keep its requested width.
        data.Rect.Right = data.Rect.Left + _desiredWidthPixels;
        if (SHAppBarMessage(AbmSetPos, ref data) == UIntPtr.Zero) return false;

        var approved = data.Rect;
        var width = approved.Right - approved.Left;
        var height = approved.Bottom - approved.Top;
        if (width <= 0 || height <= 0 || width > screenWidth || height > screenHeight
            || approved.Left < 0 || approved.Top < 0 || approved.Right > screenWidth || approved.Bottom > screenHeight)
            return false;

        var monitor = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
        var monitorInfo = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo)) return false;

        var layout = CalculateLayout(
            new DesktopRect(approved.Left, approved.Top, approved.Right, approved.Bottom),
            new DesktopRect(monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
                monitorInfo.Monitor.Right, monitorInfo.Monitor.Bottom),
            new DesktopRect(monitorInfo.Work.Left, monitorInfo.Work.Top,
                monitorInfo.Work.Right, monitorInfo.Work.Bottom), _fullScreen);
        var bounds = layout.ApprovedBounds;
        if (layout.WorkArea.Width <= 0 || layout.WorkArea.Height <= 0) return false;
        if (!SetWindowPos(_hwnd, HwndTopmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                SwpNoActivate | SwpNoOwnerZOrder))
            return false;

        if (!GetWindowRect(_hwnd, out var actual)
            || actual.Left != bounds.Left
            || actual.Top != bounds.Top
            || actual.Right != bounds.Right
            || actual.Bottom != bounds.Bottom)
            return false;
        if (Layout != layout)
        {
            Layout = layout;
            LayoutChanged?.Invoke(layout);
        }
        return true;
    }

    internal static AppBarLayout CalculateLayout(DesktopRect approved, DesktopRect monitor,
        DesktopRect workArea, bool fullScreen)
    {
        // Hiding the taskbar leaves its Shell work-area reservation intact. Cover
        // that strip locally without changing the global work area or other apps.
        return fullScreen
            ? new AppBarLayout(
                new DesktopRect(approved.Left, monitor.Top, approved.Right, monitor.Bottom),
                new DesktopRect(approved.Right, monitor.Top, monitor.Right, monitor.Bottom))
            : new AppBarLayout(approved, workArea);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_registered && (uint)message == _callbackMessage)
        {
            var notification = wParam.ToInt64();
            if (notification == AbnPosChanged || notification == AbnStateChange)
            {
                if (!_positioning && !Reposition()) ReportRegistrationLost("Windows 未能批准 Dock 的新位置。");
            }
            else if (notification == AbnFullScreenApp)
            {
                var fullscreenOpening = lParam != IntPtr.Zero;
                GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcessId);
                // Our own borderless focus surface can also trigger this Shell
                // notification. Returning home must not bury its own Dock.
                if (foregroundProcessId == (uint)Environment.ProcessId) fullscreenOpening = false;
                _window.Topmost = !fullscreenOpening;
                if (!SetWindowPos(_hwnd, fullscreenOpening ? HwndBottom : HwndTopmost,
                        0, 0, 0, 0, SwpNoActivate | SwpNoOwnerZOrder | SwpNoMove | SwpNoSize))
                    ReportRegistrationLost("Windows 未能更新全屏应用通知后的 Dock 层级。");
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (_registered && message == WmActivate)
        {
            var data = CreateData();
            SHAppBarMessage(AbmActivate, ref data);
        }
        else if (_registered && message == WmWindowPosChanged)
        {
            var data = CreateData();
            SHAppBarMessage(AbmWindowPosChanged, ref data);
        }
        else if (_registered && (message == WmSettingChange || message == WmDisplayChange || message == WmDpiChanged))
        {
            if (message == WmDpiChanged) RecalculateDesiredWidth();
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_registered && !Reposition()) ReportRegistrationLost("Windows 未能按新的屏幕/DPI 设置定位 Dock。");
            }));
        }

        return IntPtr.Zero;
    }

    private APPBARDATA CreateData() => new()
    {
        Size = Marshal.SizeOf<APPBARDATA>(),
        Window = _hwnd,
    };

    private void RemoveHook()
    {
        if (_source == null) return;
        _source.RemoveHook(_hook);
        _source = null;
    }

    private void ReportRegistrationLost(string reason)
    {
        if (_registrationLossReported) return;
        _registrationLossReported = true;
        RegistrationLost?.Invoke(reason);
    }

    private static uint GetDpi(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private void RecalculateDesiredWidth() =>
        _desiredWidthPixels = Math.Max(1,
            (int)Math.Round(_desiredWidthDip * GetDpi(_hwnd) / 96d, MidpointRounding.AwayFromZero));

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public RECT Rect;
        public IntPtr LParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
