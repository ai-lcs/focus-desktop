using SwmColor = System.Windows.Media.Color;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using focus_desktop.Services;

namespace focus_desktop;

/// <summary>
/// 番茄钟卡片（设计参考 Kevin 桌面番茄钟.pyw：环形进度 + 青绿/金配色 + 15/30/45/60 模式 + 完成蜂鸣）。
/// 纯代码构建（无 XAML）；配色在暖色主题上适配（卡片近黑、环青绿、标题金）。
/// </summary>
public class PomodoroControl : System.Windows.Controls.UserControl
{
    // 配色（桌面番茄钟.pyw 同源）
    private static readonly Brush Teal = new SolidColorBrush(SwmColor.FromRgb(0x00, 0xC9, 0xA7));
    private static readonly Brush TealHover = new SolidColorBrush(SwmColor.FromRgb(0x33, 0xDF, 0xC0));
    private static readonly Brush Gold = new SolidColorBrush(SwmColor.FromRgb(0xF0, 0xB4, 0x29));
    private static readonly Brush Track = new SolidColorBrush(SwmColor.FromRgb(0x2E, 0x32, 0x38));
    private static readonly Brush White = new SolidColorBrush(SwmColor.FromRgb(0xF0, 0xF4, 0xFF));
    private static readonly Brush Muted = new SolidColorBrush(SwmColor.FromRgb(0x8A, 0x8F, 0x96));
    private static readonly Brush Blue = new SolidColorBrush(SwmColor.FromRgb(0x4D, 0x7E, 0xFF));

    private static readonly int[] Modes = { 15, 30, 45, 60 };

    private readonly PomodoroService _svc = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private readonly Canvas _ringCanvas = new() { Width = 150, Height = 150 };
    private readonly TextBlock _timeText = new()
    {
        FontSize = 32, FontWeight = FontWeights.Bold,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Foreground = White, TextAlignment = TextAlignment.Center, Width = 150,
    };
    private readonly TextBlock _statusText = new()
    {
        FontSize = 11, TextAlignment = TextAlignment.Center, Width = 150,
    };
    private TextBlock _phaseLabel = null!;
    private readonly TextBlock _cycleText = new() { FontSize = 11, Foreground = Muted };
    private readonly Dictionary<int, Button> _modeBtns = new();
    private Button _btnStart = null!, _btnPause = null!, _btnReset = null!;
    private bool _lastFinished; // 完成蜂鸣只响一次

    public PomodoroControl()
    {
        BuildUi();
        _svc.Tick += OnSvcTick;
        _svc.PhaseChanged += OnSvcPhaseChanged;
        _uiTimer.Tick += (_, _) => Redraw();
        _uiTimer.Start();
        Redraw();
    }

    private void OnSvcTick() => Dispatcher.Invoke(() =>
    {
        _svc.OnSecond();
        Redraw();
    });

    private void OnSvcPhaseChanged() => Dispatcher.Invoke(Redraw);

    public void LoadConfig(AppSettings cfg)
    {
        _svc.LoadConfig(cfg);
        // 用户自定义过番茄钟时长时同步模式按钮显示
        Redraw();
    }

    // ---------------- UI 构建 ----------------

    private void BuildUi()
    {
        Background = Brushes.Transparent;
        Width = 300;

        var root = new StackPanel();

        // 无标题行（卡片上下文已明确是番茄钟）；轮数并入状态行

        // 环形进度（轨道环 + 进度弧 + 中心时间/状态）
        _ringCanvas.HorizontalAlignment = HorizontalAlignment.Center;
        _ringCanvas.Margin = new Thickness(0, 8, 0, 4);

        var track = new Ellipse { Stroke = Track, StrokeThickness = 10, Width = 124, Height = 124 };
        Canvas.SetLeft(track, 13); Canvas.SetTop(track, 13);
        _ringCanvas.Children.Add(track);
        // 金色装饰细环（轨道外圈）
        var goldRing = new Ellipse { Stroke = Gold, StrokeThickness = 1, Opacity = 0.45, Width = 140, Height = 140 };
        Canvas.SetLeft(goldRing, 5); Canvas.SetTop(goldRing, 5);
        _ringCanvas.Children.Add(goldRing);

        _phaseLabel = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Width = 150, Foreground = Muted };
        Canvas.SetLeft(_phaseLabel, 0); Canvas.SetTop(_phaseLabel, 44);
        _ringCanvas.Children.Add(_phaseLabel);
        Canvas.SetLeft(_timeText, 0); Canvas.SetTop(_timeText, 62);
        _ringCanvas.Children.Add(_timeText);
        Canvas.SetLeft(_statusText, 0); Canvas.SetTop(_statusText, 96);
        _ringCanvas.Children.Add(_statusText);

        root.Children.Add(_ringCanvas);

        // 模式按钮 15/30/45/60
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 8) };
        foreach (var m in Modes)
        {
            var b = new Button
            {
                Content = $"{m} 分钟",
                FontSize = 11,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(0),
            };
            var mins = m;
            b.Click += (_, _) => SetMode(mins);
            _modeBtns[m] = b;
            modeRow.Children.Add(b);
        }
        root.Children.Add(modeRow);

        // 操作按钮：开始 / 暂停 / 重置
        var actRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _btnStart = MkBtn("开始", (_, _) => Start(), Teal, null);
        _btnPause = MkBtn("暂停", (_, _) => Pause(), null, null);
        _btnReset = MkBtn("重置", (_, _) => Reset(), null, null);
        actRow.Children.Add(_btnStart);
        actRow.Children.Add(_btnPause);
        actRow.Children.Add(_btnReset);
        root.Children.Add(actRow);

        Content = root;
        StyleModeButtons();
    }

    private Button MkBtn(string text, RoutedEventHandler onClick, Brush? bg, object? ignored = null)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(4, 0, 4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(0),
        };
        b.Click += onClick;
        return b;
    }

    // ---------------- 交互 ----------------

    private void SetMode(int minutes)
    {
        if (_svc.IsRunning) return; // 运行中不许改模式（桌面版同款规则）
        _svc.WorkMinutes = minutes;
        _svc.Reset();
        _lastFinished = false;
        StyleModeButtons();
        Redraw();
    }

    private void Start()
    {
        _svc.Start();
        Redraw();
    }

    private void Pause()
    {
        if (_svc.IsRunning) _svc.Pause(); else if (_svc.CurrentPhase != PomodoroService.Phase.Idle) _svc.Resume();
        Redraw();
    }

    private void Reset()
    {
        _svc.Reset();
        _lastFinished = false;
        Redraw();
    }

    // ---------------- 绘制 ----------------

    private void Redraw()
    {
        var remain = _svc.Remaining();
        _timeText.Text = _svc.CurrentPhase == PomodoroService.Phase.Idle
            ? TimeSpan.FromMinutes(_svc.WorkMinutes).ToString(@"mm\:ss")
            : remain.ToString(@"mm\:ss");
        // 阶段标签（环内顶部）：Idle=番茄钟 / Work=专注 / Break=休息 —— 一眼看出在哪个阶段
        var (phaseName, phaseBrush) = _svc.CurrentPhase switch
        {
            PomodoroService.Phase.Idle => ("番茄钟", Muted),
            PomodoroService.Phase.Work => ("专注", TealHover),
            _ => ("休息", Gold),
        };
        var paused = _svc.CurrentPhase != PomodoroService.Phase.Idle && !_svc.IsRunning;
        _phaseLabel.Text = paused ? $"{phaseName}·已暂停" : phaseName;
        _phaseLabel.Foreground = paused ? Blue : phaseBrush;

        // 状态行（环内底部）：idle 显示规则脚注（自我解释），运行时显示轮数
        string status;
        if (_svc.CurrentPhase == PomodoroService.Phase.Idle)
            status = $"{_svc.WorkMinutes} 分钟 · 每 {_svc.CyclesUntilLong} 轮长休 {_svc.LongBreakMinutes} 分";
        else if (_svc.CompletedCycles > 0)
            status = $"第 {_svc.CompletedCycles} 轮 · 下一步：{(_svc.CurrentPhase == PomodoroService.Phase.Work ? "休息" : "专注")}";
        else
            status = $"下一步：休息";
        _statusText.Text = status;
        _statusText.Foreground = paused ? Blue : Muted;

        // 按钮状态机：Idle=[开始] Running=[暂停+重置] Paused=[继续+重置]
        var idle = _svc.CurrentPhase == PomodoroService.Phase.Idle;
        var running = _svc.IsRunning;
        _btnStart.Content = idle ? "开始" : "…";
        _btnStart.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        _btnPause.Content = running ? "暂停" : "继续";
        _btnPause.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        _btnReset.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        StyleModeButtons();
        StyleActionButtons();

        // 环形进度弧
        DrawArc(remain);

        // 完成检测（阶段刚切换到休息 = 一个专注段结束）
        if (_svc.CurrentPhase is PomodoroService.Phase.ShortBreak or PomodoroService.Phase.LongBreak
            && !_lastFinished && _svc.CompletedCycles > 0)
        {
            _lastFinished = true;
            Beep();
        }
        else if (_svc.CurrentPhase == PomodoroService.Phase.Work)
        {
            _lastFinished = false;
        }
    }

    private void DrawArc(TimeSpan remain)
    {
        // 移除旧弧（0=轨道椭圆 1=金色装饰环，弧追加在后）
        for (int i = _ringCanvas.Children.Count - 1; i >= 2; i--)
            _ringCanvas.Children.RemoveAt(i);

        if (_svc.CurrentPhase == PomodoroService.Phase.Idle || _svc.CurrentPhaseMinutes <= 0) return;

        var total = TimeSpan.FromMinutes(_svc.CurrentPhaseMinutes);
        var ratio = total > TimeSpan.Zero ? remain / total : 0;
        ratio = Math.Max(0, Math.Min(1.0, ratio));
        if (ratio <= 0) return;

        var color = !_svc.IsRunning ? Blue
            : _svc.CurrentPhase == PomodoroService.Phase.Work ? TealHover
            : Gold;

        var cx = 75.0; var cy = 75.0; var R = 62.0;
        var angle = -ratio * 360.0; // 顺时针（y 向下坐标系）
        var rad = angle * Math.PI / 180.0;
        var start = new System.Windows.Point(cx, cy - R);
        var end = new System.Windows.Point(cx + R * Math.Sin(rad), cy - R * Math.Cos(rad));
        var large = ratio > 0.5;

        var arc = new Path
        {
            Stroke = color,
            StrokeThickness = 10,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = new PathGeometry
            {
                Figures =
                {
                    new PathFigure
                    {
                        StartPoint = start,
                        Segments = { new ArcSegment(end, new System.Windows.Size(R, R), 0, large, SweepDirection.Clockwise, true) }
                    }
                }
            }
        };
        _ringCanvas.Children.Add(arc);
    }

    private void StyleModeButtons()
    {
        foreach (var (m, b) in _modeBtns)
        {
            var active = m == _svc.WorkMinutes && !_svcHasCustomMinutes;
            b.Background = active ? Teal : new SolidColorBrush(SwmColor.FromRgb(0x23, 0x23, 0x2A));
            b.Foreground = active ? Brushes.Black : new SolidColorBrush(SwmColor.FromRgb(0xCF, 0xCF, 0xCF));
        }
    }

    private bool _svcHasCustomMinutes;

    private void StyleActionButtons()
    {
        // 主键（青绿高亮）：Idle=开始；Running=暂停变次键、无主键强调；Paused=继续当主键
        var idle = _svc.CurrentPhase == PomodoroService.Phase.Idle;
        var running = _svc.IsRunning;
        _btnStart.Background = Teal;
        _btnStart.Foreground = Brushes.Black;
        if (idle)
        {
            _btnPause.Background = new SolidColorBrush(SwmColor.FromRgb(0x23, 0x23, 0x2A));
            _btnPause.Foreground = White;
        }
        else if (running)
        {
            _btnPause.Background = new SolidColorBrush(SwmColor.FromRgb(0x23, 0x23, 0x2A));
            _btnPause.Foreground = White;
        }
        else // paused → 继续是主键
        {
            _btnPause.Background = Teal;
            _btnPause.Foreground = Brushes.Black;
        }
        _btnReset.Background = new SolidColorBrush(SwmColor.FromRgb(0x23, 0x23, 0x2A));
        _btnReset.Foreground = new SolidColorBrush(SwmColor.FromRgb(0xCF, 0xCF, 0xCF));
    }

    /// <summary>完成一段专注：蜂鸣一声（桌面版 winsound.Beep(660,400) 的移植）。</summary>
    private static void Beep()
    {
        try
        {
            // System.Media.SystemSounds 只有系统音；用 Console.Beep 的异步版本（线程池）
            System.Threading.Tasks.Task.Run(() =>
            {
                try { System.Console.Beep(660, 400); } catch { }
            });
        }
        catch { }
    }
}
