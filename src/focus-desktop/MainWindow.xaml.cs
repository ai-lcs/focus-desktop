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
        App.SmokeLog("window: content rendered");
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        // Step 1+2：直接退出（钩子 WM_CLOSE 防线因焦点模式即将退出而放行）
        // Step 6 会在这里插入退出文本验证
        App.SmokeLog("exit button clicked");
        Application.Current.Shutdown();
    }
}
