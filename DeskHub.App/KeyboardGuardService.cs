using System.Runtime.InteropServices;

namespace FocusDesktop;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）。P/Invoke 移植自 class-lock src/keyboard_hook.py（MIT），
/// 拦截表经 SafeExamBrowser KeyboardInterceptor.cs（MPL-2.0，只读参考）校对。
///
/// 关键事实：WebView2 是独立子 HWND，WPF PreviewKeyDown 看不见其中按键，
/// 只有系统级 LL 钩子能在消息分发前拦截，与焦点所在进程无关。
///
/// 回调内只做查表判断（保持 &lt;1ms），不做任何复杂逻辑，
/// 避免被系统因超时摘除钩子（Windows 会静默 bypass 慢钩子）。
/// </summary>
public sealed class KeyboardGuardService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int LLKHF_ALTDOWN = 0x20;

    private const int VK_ESCAPE = 0x1B;
    private const int VK_TAB = 0x09;
    private const int VK_LWIN = 0x5B;
    private const int VK_SPACE = 0x20;
    // VK 常量表（class-lock 同源）
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;    // Alt
    private const int VK_F4 = 0x73;
    private const int VK_RWIN = 0x5C;

    private const nint LRESULT_BLOCK = 1; // 非零返回值 = 吞掉按键

    private readonly LowLevelKeyboardProc _proc;
    private nint _hookId = nint.Zero;
    private bool _disposed;

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    public KeyboardGuardService()
    {
        // 防止回调委托被 GC 回收——必须持引用
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != nint.Zero) return;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(null), 0);
        if (_hookId == nint.Zero)
            throw new InvalidOperationException(
                $"键盘钩子安装失败 (GetLastError={Marshal.GetLastWin32Error()})");
    }

    public void Uninstall()
    {
        if (_hookId == nint.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
    }

    /// <summary>仅安装时输出诊断用（自检钩子是否真的在拦）。</summary>
    public static bool ShouldBlock(int vk, bool altDown, bool ctrlDown)
        => BlockDecision(vk, altDown, ctrlDown);

    private static bool BlockDecision(int vk, bool altDown, bool ctrlDown)
    {
        // 1) Alt 组合（WM_SYSKEYDOWN 时 LLKHF_ALTDOWN 置位）
        //    Alt+Tab / Alt+Esc / Alt+F4 / Alt+Space
        if (altDown && (vk == VK_TAB || vk == VK_ESCAPE || vk == VK_F4 || vk == VK_SPACE))
            return true;

        // 2) Win 键本身（按下即吞，开始菜单不出）
        if (vk == VK_LWIN || vk == VK_RWIN)
            return true;

        // 3) Ctrl+Esc（开始菜单的键盘替代路径）
        if (ctrlDown && vk == VK_ESCAPE)
            return true;

        // 4) Esc：零处理——B 站网页全屏退出依赖它，无边界窗口本也关不掉

        return false;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool altDown = (info.flags & LLKHF_ALTDOWN) != 0;
            bool ctrlDown = (GetKeyStateAsync(VK_CONTROL) & 0x8000) != 0;

            if (BlockDecision((int)info.vkCode, altDown, ctrlDown))
                return LRESULT_BLOCK; // 吞掉，不传递给系统
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static short GetKeyStateAsync(int vk) => GetAsyncKeyState(vk);

    public void Dispose()
    {
        if (_disposed) return;
        Uninstall();
        _disposed = true;
    }
}
