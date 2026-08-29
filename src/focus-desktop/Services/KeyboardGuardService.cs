using System.Runtime.InteropServices;

namespace focus_desktop.Services;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）。翻译自 class-lock src/keyboard_hook.py（MIT），
/// 拦截策略对照 SEB KeyboardInterceptor.cs（MPL-2.0，仅参考未复制）。
///
/// 为什么必须用全局 LL 钩子而不是 WPF 键事件：WebView2 是独立子 HWND，焦点在
/// 网页内时键盘消息直接进 WebView2 进程，WPF 的 PreviewKeyDown 根本看不见。
/// LL 钩子在系统分发消息之前拦截，与焦点在哪个进程无关。
///
/// 纪律：回调只做查表判断，必须极快返回（慢回调会被系统摘除钩子）。
/// Esc 一律放行（Bilibili 网页全屏退出要用）。
/// </summary>
public sealed class KeyboardGuardService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_F4 = 0x73;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // 委托必须存字段保活：SetWindowsHookEx 只存函数指针，GC 回收委托 = 回调进已释放内存 = 崩溃
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public bool IsInstalled => _hookId != IntPtr.Zero;

    public KeyboardGuardService()
    {
        _proc = HookCallback;
    }

    /// <summary>安装钩子。须在带消息循环的 UI 线程调用（WPF 主线程满足）。</summary>
    public void Install()
    {
        if (IsInstalled) return;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(module.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    public void Dispose() => Uninstall();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        try
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt64();

            if (ShouldBlock(msg, key.VkCode))
            {
                return new IntPtr(1); // 吞掉：不进入系统分发
            }
        }
        catch
        {
            // 回调里出任何异常都放行并交给下一环，绝不让钩子链断掉
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>拦截判定。基本款（用户已拍板）：Alt 组合 + Win 键系 + Ctrl+Esc。</summary>
    private static bool ShouldBlock(long message, uint vk)
    {
        bool IsDown(int m) => message == m;

        // Win 键本身：down 全拦（up 也拦，防止按下瞬间开始菜单闪出）
        if (vk is VK_LWIN or VK_RWIN)
        {
            return true;
        }

        // Alt 组合（WM_SYSKEYDOWN）
        if (IsDown(WM_SYSKEYDOWN) && AltHeld())
        {
            // Alt+Tab / Alt+F4 / Alt+Esc / Alt+Space（系统菜单）
            if (vk is VK_TAB or VK_F4 or VK_ESCAPE or VK_SPACE)
            {
                return true;
            }
        }

        // Ctrl+Esc（开始菜单的另一条路）
        if (IsDown(WM_KEYDOWN) && vk == VK_ESCAPE && CtrlHeld())
        {
            return true;
        }

        return false;
    }

    private static bool AltHeld() => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
    private static bool CtrlHeld() => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
}
