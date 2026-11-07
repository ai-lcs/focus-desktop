using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace focus_desktop.Services;

internal static class WindowTransitions
{
    internal static void SetVisible(Window window, bool visible)
    {
        // Cloak the native window while AppBar/Shell layout is changing. WPF
        // opacity alone does not establish when DWM has hidden the window.
        var handle = new WindowInteropHelper(window).EnsureHandle();
        window.Opacity = visible ? 1 : 0;
        var cloak = visible ? 0 : 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 13, ref cloak, sizeof(int)));
    }

    internal static void Disable(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var disabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 3, ref disabled, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
