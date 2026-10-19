using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class DockWindowSnapshot
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SpiGetWorkArea = 0x0030;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly Guid PropertyStoreIid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AppUserModelIdFormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    public static DesktopSnapshot Capture()
    {
        IntPtr previousDpiContext = IntPtr.Zero;
        var setDpiContext = false;
        try
        {
            previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            setDpiContext = previousDpiContext != IntPtr.Zero;
        }
        catch (EntryPointNotFoundException) { }

        try { return CaptureCore(setDpiContext); }
        finally
        {
            if (setDpiContext)
                SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }

    private static DesktopSnapshot CaptureCore(bool perMonitorV2)
    {
        var result = new DesktopSnapshot();
        result.DpiAwarenessContext = perMonitorV2 ? "per-monitor-v2" : "unchanged";
        var foreground = GetForegroundWindow();
        result.ForegroundHandle = foreground == IntPtr.Zero ? "" : "0x" + foreground.ToInt64().ToString("X");
        uint foregroundPid;
        GetWindowThreadProcessId(foreground, out foregroundPid);
        result.ForegroundProcessId = foregroundPid == 0 ? (int?)null : (int)foregroundPid;

        var workArea = new NativeRect();
        if (SystemParametersInfo(SpiGetWorkArea, 0, ref workArea, 0))
            result.PrimaryWorkArea = WindowRect.From(workArea);

        var monitorCallback = new MonitorEnumProc((monitor, deviceContext, rectangle, parameter) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (GetMonitorInfo(monitor, ref info))
                result.Monitors.Add(new MonitorSnapshot
                {
                    Bounds = WindowRect.From(info.Monitor),
                    WorkArea = WindowRect.From(info.Work),
                    IsPrimary = (info.Flags & 1) != 0
                });
            return true;
        });
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, monitorCallback, IntPtr.Zero);
        GC.KeepAlive(monitorCallback);

        var callback = new EnumWindowsProc((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            var bounds = new NativeRect();
            if (!GetWindowRect(handle, ref bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
                return true;

            uint pid;
            GetWindowThreadProcessId(handle, out pid);
            if (pid == 0)
                return true;

            var owner = GetWindow(handle, 4); // GW_OWNER
            uint ownerPid;
            GetWindowThreadProcessId(owner, out ownerPid);
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            WindowRect monitorWorkArea = null;
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
                monitorWorkArea = WindowRect.From(monitorInfo.Work);

            result.Windows.Add(new WindowSnapshot
            {
                Handle = "0x" + handle.ToInt64().ToString("X"),
                ProcessId = (int)pid,
                ProcessName = GetProcessName((int)pid),
                OwnerProcessId = ownerPid == 0 ? (int?)null : (int)ownerPid,
                ClassName = GetClassNameFor(handle),
                AppUserModelId = GetAppUserModelId(handle),
                ProcessAppUserModelId = GetProcessAppUserModelId((int)pid),
                Visible = true,
                Topmost = (GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExTopmost) != 0,
                State = GetWindowState(handle),
                Dpi = GetDpiForWindow(handle),
                Bounds = WindowRect.From(bounds),
                MonitorWorkArea = monitorWorkArea
            });
            return true;
        });

        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        result.CapturedAtUtc = DateTime.UtcNow.ToString("O");
        return result;
    }

    private static string GetProcessName(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return process.ProcessName; }
        catch { return "(unavailable)"; }
    }

    private static string GetClassNameFor(IntPtr handle)
    {
        var text = new StringBuilder(256);
        return GetClassName(handle, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    private static string GetWindowState(IntPtr handle)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf(typeof(WindowPlacement)) };
        if (!GetWindowPlacement(handle, ref placement))
            return "unknown";
        switch (placement.ShowCommand)
        {
            case 2: return "minimized";
            case 3: return "maximized";
            case 1: return "normal";
            default: return "showcmd-" + placement.ShowCommand;
        }
    }

    private static string GetAppUserModelId(IntPtr handle)
    {
        IPropertyStore store = null;
        try
        {
            var iid = PropertyStoreIid;
            if (SHGetPropertyStoreForWindow(handle, ref iid, out store) != 0 || store == null)
                return "";

            var key = new PropertyKey { FormatId = AppUserModelIdFormatId, PropertyId = 5 };
            PropVariant value;
            if (store.GetValue(ref key, out value) != 0)
                return "";

            try
            {
                if ((value.VariantType == 31 || value.VariantType == 8) && value.PointerValue != IntPtr.Zero)
                    return Marshal.PtrToStringUni(value.PointerValue) ?? "";
                return "";
            }
            finally { PropVariantClear(ref value); }
        }
        catch { return ""; }
        finally { if (store != null && Marshal.IsComObject(store)) Marshal.ReleaseComObject(store); }
    }

    private static string GetProcessAppUserModelId(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero)
            return "";
        try
        {
            uint length = 0;
            var status = GetApplicationUserModelId(process, ref length, null);
            if (status != ErrorInsufficientBuffer || length == 0)
                return "";

            var value = new StringBuilder((int)length);
            status = GetApplicationUserModelId(process, ref length, value);
            return status == 0 ? value.ToString() : "";
        }
        catch (EntryPointNotFoundException) { return ""; }
        finally { CloseHandle(process); }
    }

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new IntPtr(GetWindowLong32(handle, index));
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr rectangle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public Point MinPosition;
        public Point MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    public sealed class DesktopSnapshot
    {
        public DesktopSnapshot()
        {
            CapturedAtUtc = "";
            PrimaryWorkArea = new WindowRect();
            Windows = new List<WindowSnapshot>();
            Monitors = new List<MonitorSnapshot>();
            DpiAwarenessContext = "";
            ForegroundHandle = "";
        }
        public string CapturedAtUtc { get; set; }
        public string DpiAwarenessContext { get; set; }
        public string ForegroundHandle { get; set; }
        public int? ForegroundProcessId { get; set; }
        public WindowRect PrimaryWorkArea { get; set; }
        public List<WindowSnapshot> Windows { get; set; }
        public List<MonitorSnapshot> Monitors { get; set; }
    }

    public sealed class WindowSnapshot
    {
        public WindowSnapshot()
        {
            Handle = "";
            ProcessName = "";
            ClassName = "";
            AppUserModelId = "";
            ProcessAppUserModelId = "";
            State = "";
            Bounds = new WindowRect();
        }
        public string Handle { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public int? OwnerProcessId { get; set; }
        public string ClassName { get; set; }
        public string AppUserModelId { get; set; }
        public string ProcessAppUserModelId { get; set; }
        public bool Visible { get; set; }
        public bool Topmost { get; set; }
        public string State { get; set; }
        public uint Dpi { get; set; }
        public WindowRect Bounds { get; set; }
        public WindowRect MonitorWorkArea { get; set; }
    }

    public sealed class MonitorSnapshot
    {
        public MonitorSnapshot()
        {
            Bounds = new WindowRect();
            WorkArea = new WindowRect();
        }
        public WindowRect Bounds { get; set; }
        public WindowRect WorkArea { get; set; }
        public bool IsPrimary { get; set; }
    }

    public sealed class WindowRect
    {
        public WindowRect() { }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public static WindowRect From(NativeRect rectangle)
        {
            return new WindowRect { Left = rectangle.Left, Top = rectangle.Top, Right = rectangle.Right, Bottom = rectangle.Bottom };
        }
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRectangle, MonitorEnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, ref NativeRect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, ref NativeRect value, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("shell32.dll", PreserveSig = true)] private static extern int SHGetPropertyStoreForWindow(IntPtr handle, ref Guid iid, out IPropertyStore store);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint applicationUserModelIdLength, StringBuilder applicationUserModelId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
