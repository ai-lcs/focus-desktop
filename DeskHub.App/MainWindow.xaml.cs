using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FocusDesktop;

public partial class MainWindow : Window
{
    private readonly bool _devMode;
    private readonly RecoveryService _recovery = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    // 计时器状态（Focuser pomodoro.rs 模型：时间戳 + 冻结剩余）
    private DateTime _phaseStart;
    private TimeSpan _elapsedWhenPaused;
    private bool _timerRunning;

    public MainWindow(bool devMode)
    {
        _devMode = devMode;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            FillPrimaryScreen();
        };
        Loaded += (_, _) =>
        {
            if (App.DevMode)
            {
                DevBadge.Visibility = Visibility.Visible;
                _recovery.EnterFocusMode();
            }
            else
            {
                // 正式模式：启动即锁定（首次设置模式在 Step 3 加入）
                _recovery.EnterFocusMode();
            }
            _recoveryExitDone = false;
        };
        Closing += (_, e) =>
        {
            // 退出前必须走恢复路径；--dev 同样恢复
            if (!_recoveryExitDone)
            {
                _recovery.ExitFocusMode();
                _recoveryExitDone = true;
            }
        };
        Closed += (_, _) => _recovery.Dispose();

        _clock.Tick += (_, _) => TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();
        TimeText.Text = DateTime.Now.ToString("HH:mm:ss");

        _timer.Tick += (_, _) =>
        {
            if (_timerRunning)
            {
                var elapsed = _elapsedWhenPaused + (DateTime.Now - _phaseStart);
                TimerBig.Text = elapsed.ToString(@"hh\:mm\:ss");
            }
        };
    }

    private bool _recoveryExitDone;

    private void FillPrimaryScreen()
    {
        // 单显示器：铺满主屏（spec 锁定，多屏逻辑一行不写）
        // WPF 中主屏原点恒为 (0,0)，无 PrimaryScreenLeft/Top 属性
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc 不拦（B 站网页全屏）；Space 给计时器；F11 开发诊断
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            ToggleTimer();
        }
        else if (e.Key == Key.F11 && App.DevMode)
        {
            e.Handled = true;
            StatusText.Text = $"hook={(_recovery.IsInFocusMode ? "on" : "off")} " +
                              $"taskbarHidden={TaskbarService.IsHidden()} " +
                              $"dirty={RecoveryService.WasUncleanShutdown()}";
        }
    }

    private void ToggleTimer()
    {
        if (_timerRunning)
        {
            _elapsedWhenPaused += DateTime.Now - _phaseStart;
            _timerRunning = false;
            TimerHint.Text = "已暂停 · 空格继续 · R 重置";
        }
        else
        {
            _phaseStart = DateTime.Now;
            _timerRunning = true;
            TimerHint.Text = "计时中 · 空格暂停";
        }
        _timer.Start();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        // Step 1+2 阶段：--dev 点击即退（方便测试恢复路径）
        // Step 6 换成退出文本验证
        if (App.DevMode)
        {
            Close(); // Closing 事件里走 ExitFocusMode
        }
        else
        {
            Close();
        }
    }
}
