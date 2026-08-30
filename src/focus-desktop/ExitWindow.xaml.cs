using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using focus_desktop.Services;

namespace focus_desktop;

/// <summary>
/// 退出验证（Step 6）。独立置顶窗口：
/// 1) 不受主窗口 Grid 布局影响（旧版弹窗被压进顶栏行导致不可见——2026-08-30 事故根因）
/// 2) 不受 WindowsFormsHost airspace 影响（永远画在 WebView2 之上）
/// 3) Loaded 后做物理屏幕钳制（2026-08-30 第二起事故：高 DPI 下 CenterScreen 算出的
///    位置超出物理屏幕，确认按钮在屏幕外，用户被迫用任务管理器杀进程）
/// 用法：var w = new ExitWindow(phrase); if (w.ShowDialog() == true) → 允许退出
/// </summary>
public partial class ExitWindow : Window
{
    private readonly string _expected;

    public ExitWindow()
    {
        InitializeComponent();
        _expected = "";
    }

    public ExitWindow(string phrase) : this()
    {
        _expected = phrase.Trim();
        PhraseText.Text = phrase;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // 构造时即用已知尺寸逻辑居中（SizeToContent+CenterScreen 在 DPI 200% 下曾把弹窗
        // 顶出屏幕外，确认按钮点不到——2026-08-30 事故；固定尺寸让计算可预期）
        try
        {
            var sw = SystemParameters.PrimaryScreenWidth;
            var sh = SystemParameters.PrimaryScreenHeight;
            Left = Math.Max(0, (sw - 560) / 2);
            Top = Math.Max(0, (sh - 400) / 2);
        }
        catch { }
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            ClampToPhysicalScreen();
        };
    }

    /// <summary>
    /// 把窗口物理矩形钳进物理屏幕内（GetWindowRect → 越界则 MoveWindow）。
    /// WPF CenterScreen 在混合 DPI/虚拟化环境下可能给出屏幕外坐标——这是最后防线。
    /// </summary>
    private void ClampToPhysicalScreen()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (!GetWindowRect(hwnd, out var r)) return;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            var sw = GetSystemMetrics(SM_CXSCREEN);
            var sh = GetSystemMetrics(SM_CYSCREEN);
            if (w <= 0 || h <= 0 || sw <= 0 || sh <= 0) return;

            // 越界钳制：优先贴边可见，保证完整落在屏幕内
            var x = Math.Max(0, Math.Min(r.Left, sw - w));
            var y = Math.Max(0, Math.Min(r.Top, sh - h));
            if (x != r.Left || y != r.Top)
            {
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
        catch { }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void TryConfirm()
    {
        if (InputBox.Text.Trim() == _expected)
        {
            DialogResult = true;
        }
        else
        {
            ErrorText.Text = "输入不一致，请核对后重试";
            InputBox.SelectAll();
            InputBox.Focus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Enter) { TryConfirm(); e.Handled = true; }
    }

    // ---- Win32 ----
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}
