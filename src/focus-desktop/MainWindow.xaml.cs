using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using focus_desktop.Services;

namespace focus_desktop;

public partial class MainWindow : Window
{
    private readonly AppOptions _options;
    private readonly FocusModeService _focus;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(AppOptions options, FocusModeService focus)
    {
        _options = options;
        _focus = focus;
        InitializeComponent();

        // 铺满主屏（单显示器项目，按用户决策不做多屏）
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Topmost = true;

        if (options.Dev)
        {
            DevBadge.Visibility = Visibility.Visible;
        }

        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Alt+F4 的第二道防线：直接吃掉 WM_CLOSE（钩子是第一道）
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        App.SmokeLog("window: source initialized, bounds set");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CLOSE = 0x0010;
        if (msg == WM_CLOSE && _focus.IsActive)
        {
            // 锁定期间不响应系统关闭（退出只能走软件内按钮）
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 显式夺取前台：Windows ForegroundLockTimeout 可能阻止新进程抢焦点，
        // 导致全屏窗口盖住屏幕但键盘焦点留在身后的窗口——kiosk 致命缺陷
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetForegroundWindow(hwnd);
        App.SmokeLog("window: content rendered, foreground claimed");
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        // 先解锁再关窗：WndProc 的 WM_CLOSE 防线查 _focus.IsActive，
        // 若先 Shutdown 后 Exit，Shutdown 发的 WM_CLOSE 会被自己防线吃掉 → 窗口关不掉
        _focus.Exit();
        // Step 6 会在这里插入退出文本验证
        App.SmokeLog("exit button clicked");
        Application.Current.Shutdown();
    }
}
