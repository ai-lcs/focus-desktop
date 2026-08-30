using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using focus_desktop.Services;
using SwmColor = System.Windows.Media.Color;

namespace focus_desktop;

/// <summary>
/// 番茄钟卡片内容（桌面番茄钟.pyw 设计语言：环形进度+青绿/金配色+蜂鸣）。
/// v0.3.2 简化（用户指示"不要加文字"）：环内只留倒计时数字，阶段用弧颜色表达；
/// 修复：近 360° 退化弧（ArcSegment 起终点重合 → 未定义行为 → 巨大圆环渲染异常）。
/// </summary>
public class PomodoroControl : System.Windows.Controls.UserControl
{
    // 配色（桌面番茄钟.pyw 同源）
    private static readonly Brush Teal = new SolidColorBrush(SwmColor.FromRgb(0x00, 0xC9, 0xA7));
    private static readonly Brush TealHover = new SolidColorBrush(SwmColor.FromRgb(0x2E, 0xD9, 0xB5));
    private static readonly Brush Gold = new SolidColorBrush(SwmColor.FromRgb(0xF0, 0xB4, 0x29));
    private static readonly Brush Blue = new SolidColorBrush(SwmColor.FromRgb(0x7F, 0xB3, 0xE8));
    private static readonly Brush White = new SolidColorBrush(SwmColor.FromRgb(0xF0, 0xF4, 0xFF));
    private static readonly Brush Muted = new SolidColorBrush(SwmColor.FromRgb(0x9A, 0xA0, 0xA6));
    private static readonly Brush Track = new SolidColorBrush(SwmColor.FromRgb(0x2E, 0x2E, 0x36));
    private static readonly Brush BtnBg = new SolidColorBrush(SwmColor.FromRgb(0x23, 0x23, 0x2A));

    private static readonly int[] Modes = { 15, 25, 30, 45, 60 };

    private readonly PomodoroService _svc = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // 环形画布 200px；ClipToBounds 防任何绘制异常溢出卡片
    private readonly Canvas _ringCanvas = new() { Width = 200, Height = 200, ClipToBounds = true };
    private System.Windows.Shapes.Path? _arcPath; // 独立引用管理弧（绝不按索引删子元素）

    private readonly TextBlock _timeText = new()
    {
        FontSize = 34, FontWeight = FontWeights.Light,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Foreground = White, TextAlignment = TextAlignment.Center, Width = 200,
    };
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
        Redraw();
    }

    // ---------------- UI 构建 ----------------

    private void BuildUi()
    {
        Background = Brushes.Transparent;
        Width = 320;

        var root = new StackPanel();

        // 标题行（与计时器卡统一）
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "番茄钟", FontSize = 13, Foreground = Muted };
        Grid.SetColumn(title, 0);
        var hint = new TextBlock { Text = "专注后自动休息 · 每 4 轮长休", FontSize = 11, Foreground = new SolidColorBrush(SwmColor.FromRgb(0x6B, 0x70, 0x76)) };
        Grid.SetColumn(hint, 1);
        titleRow.Children.Add(title);
        titleRow.Children.Add(hint);
        root.Children.Add(titleRow);

        // 环形进度：轨道 12px；环内只有倒计时（用户指示）
        _ringCanvas.HorizontalAlignment = HorizontalAlignment.Center;
        _ringCanvas.Margin = new Thickness(0, 14, 0, 10);

        var track = new Ellipse { Stroke = Track, StrokeThickness = 12, Width = 168, Height = 168 };
        Canvas.SetLeft(track, 16); Canvas.SetTop(track, 16);
        _ringCanvas.Children.Add(track);

        Canvas.SetLeft(_timeText, 0); Canvas.SetTop(_timeText, 76);
        _ringCanvas.Children.Add(_timeText);

        root.Children.Add(_ringCanvas);

        // 模式按钮 15/25/30/45/60
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var m in Modes)
        {
            var b = new Button
            {
                Content = $"{m}",
                FontSize = 12,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(0),
                ToolTip = $"{m} 分钟专注",
            };
            var mins = m;
            b.Click += (_, _) => SetMode(mins);
            _modeBtns[m] = b;
            modeRow.Children.Add(b);
        }
        root.Children.Add(modeRow);

        // 操作按钮
        var actRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _btnStart = MkBtn("开始", (_, _) => Start(), Teal);
        _btnPause = MkBtn("暂停", (_, _) => Pause(), BtnBg);
        _btnReset = MkBtn("重置", (_, _) => Reset(), BtnBg);
        actRow.Children.Add(_btnStart);
        actRow.Children.Add(_btnPause);
        actRow.Children.Add(_btnReset);
        root.Children.Add(actRow);

        Content = root;
        StyleModeButtons();
    }

    private Button MkBtn(string text, RoutedEventHandler onClick, Brush bg)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 12.5,
            Padding = new Thickness(20, 7, 20, 7),
            Margin = new Thickness(4, 0, 4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = bg,
            Foreground = bg == Teal ? Brushes.Black : White,
        };
        b.Click += onClick;
        return b;
    }

    // ---------------- 交互 ----------------

    private void SetMode(int minutes)
    {
        if (_svc.IsRunning) return;
        _svc.WorkMinutes = minutes;
        _svc.Reset();
        _lastFinished = false;
        StyleModeButtons();
        Redraw();
    }

    private void Start() { _svc.Start(); Redraw(); }

    private void Pause()
    {
        if (_svc.IsRunning) _svc.Pause(); else if (_svc.CurrentPhase != PomodoroService.Phase.Idle) _svc.Resume();
        Redraw();
    }

    private void Reset() { _svc.Reset(); _lastFinished = false; Redraw(); }

    // ---------------- 绘制 ----------------

    private void Redraw()
    {
        var remain = _svc.Remaining();
        _timeText.Text = _svc.CurrentPhase == PomodoroService.Phase.Idle
            ? TimeSpan.FromMinutes(_svc.WorkMinutes).ToString(@"mm\:ss")
            : remain.ToString(@"mm\:ss");

        // 阶段只通过弧颜色表达：专注=青绿 休息=金 暂停=蓝（无文字标签）

        // 按钮状态机：Idle=[开始] Running=[暂停+重置] Paused=[继续+重置]
        var idle = _svc.CurrentPhase == PomodoroService.Phase.Idle;
        var running = _svc.IsRunning;
        _btnStart.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        _btnPause.Content = running ? "暂停" : "继续";
        _btnPause.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        _btnReset.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        StyleModeButtons();
        StyleActionButtons();

        DrawArc(remain);

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
        // 独立引用删旧弧（绝不按索引删子元素——v0.2.0 文字被吃 bug）
        if (_arcPath != null)
        {
            _ringCanvas.Children.Remove(_arcPath);
            _arcPath = null;
        }

        if (_svc.CurrentPhase == PomodoroService.Phase.Idle || _svc.CurrentPhaseMinutes <= 0) return;

        var total = TimeSpan.FromMinutes(_svc.CurrentPhaseMinutes);
        var ratio = total > TimeSpan.Zero ? remain / total : 0;
        ratio = Math.Max(0, Math.Min(1.0, ratio));

        // 防退化弧：ratio≈1（刚启动）或 ≈0（刚结束）时起终点几乎重合，
        // ArcSegment + large-arc 行为未定义 → 曾渲染出覆盖两张卡片的巨大圆环。
        // 近整圆画完整 EllipseGeometry；近零直接不画。
        var color = !_svc.IsRunning ? Blue
            : _svc.CurrentPhase == PomodoroService.Phase.Work ? TealHover
            : Gold;

        var cx = 100.0; var cy = 100.0; var R = 84.0;

        if (ratio > 0.985)
        {
            var full = new Path
            {
                Stroke = color, StrokeThickness = 12,
                Data = new EllipseGeometry(new System.Windows.Point(cx, cy), R, R),
            };
            _arcPath = full;
            _ringCanvas.Children.Add(full);
            return;
        }
        if (ratio <= 0.005) return;

        var angle = -ratio * 360.0; // 顺时针（y 向下坐标系）
        var rad = angle * Math.PI / 180.0;
        var start = new System.Windows.Point(cx, cy - R);
        var end = new System.Windows.Point(cx + R * Math.Sin(rad), cy - R * Math.Cos(rad));
        var large = ratio > 0.5;

        var arc = new Path
        {
            Stroke = color,
            StrokeThickness = 12,
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
        _arcPath = arc;
        _ringCanvas.Children.Add(arc);
    }

    private void StyleModeButtons()
    {
        foreach (var (m, b) in _modeBtns)
        {
            var active = m == _svc.WorkMinutes;
            b.Background = active ? Teal : BtnBg;
            b.Foreground = active ? Brushes.Black : new SolidColorBrush(SwmColor.FromRgb(0xCF, 0xCF, 0xCF));
        }
    }

    private void StyleActionButtons()
    {
        var idle = _svc.CurrentPhase == PomodoroService.Phase.Idle;
        var running = _svc.IsRunning;
        if (idle || running)
        {
            _btnPause.Background = BtnBg;
            _btnPause.Foreground = White;
        }
        else
        {
            _btnPause.Background = Teal;
            _btnPause.Foreground = Brushes.Black;
        }
        _btnReset.Background = BtnBg;
        _btnReset.Foreground = new SolidColorBrush(SwmColor.FromRgb(0xCF, 0xCF, 0xCF));
    }

    /// <summary>完成一段专注：蜂鸣一声（桌面版 winsound.Beep(660,400) 移植；线程池+双 try 防无声卡崩溃）。</summary>
    private static void Beep()
    {
        try
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { System.Console.Beep(660, 400); } catch { }
            });
        }
        catch { }
    }
}
