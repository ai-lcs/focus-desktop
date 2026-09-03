using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using focus_desktop.Services;
using SwmColor = System.Windows.Media.Color;

namespace focus_desktop;

/// <summary>
/// 番茄钟卡片（v0.5.4 最终定稿）。
///
/// 结构与行为（全部为用户 2026-08-31 逐轮验收的最终形态）：
/// - 顶部居中「硬性专注」iOS 式滑块开关：开启=金轨道+白滑块右移；开始前可点关；
///   按了开始后锁定不可取消；专注段跑完进休息自动关。
///   开启时把时长拨到 30 分钟（开始前可改）。
/// - 圆环：从 12 点钟顺时针扫剩余比例的进度弧（修正后的终点数学），9px 圆头；
///   防退化阈值 0.995（避免开跑后 20 秒整圆跳缺口的「混乱」感）。
/// - 环内：大号倒计时 + 阶段小字（准备/专注/小憩/长憩）。
/// - 时长选择：Grid 五行等分列，高亮 Border 填满单元格（2px 内缩、圆角 7），
///   高亮块永远与格子对齐、间距均匀；各段 Button 用最小模板（禁用态=原样变暗，
///   无 WPF 默认 chrome 灰块）。
/// - 操作：主按钮（开始/暂停/继续，青绿药丸）+ 重置（幽灵；硬性专注开启后置暗禁点）。
/// - 硬性专注工作段进行中：时长选择整组禁点（IsEnabled=false + Opacity 0.45）。
/// 全部按钮均为自定义最小模板——消灭了两类默认 chrome 泄漏：
/// ① 默认按钮浅蓝渐变通栏横条；② 禁用态灰色块。
/// </summary>
public class PomodoroControl : System.Windows.Controls.UserControl
{
    // 配色（桌面番茄钟.pyw 同源）
    private static readonly Brush Teal = new SolidColorBrush(SwmColor.FromRgb(0x00, 0xC9, 0xA7));
    private static readonly Brush TealHover = new SolidColorBrush(SwmColor.FromRgb(0x2E, 0xD9, 0xB5));
    private static readonly Brush Gold = new SolidColorBrush(SwmColor.FromRgb(0xF0, 0xB4, 0x29));
    private static readonly Brush Blue = new SolidColorBrush(SwmColor.FromRgb(0x7F, 0xB3, 0xE8));
    private static readonly Brush White = new SolidColorBrush(SwmColor.FromRgb(0xF0, 0xF4, 0xFF));
    private static readonly Brush SubText = new SolidColorBrush(SwmColor.FromRgb(0x8A, 0x91, 0x99));
    private static readonly Brush Track = new SolidColorBrush(SwmColor.FromRgb(0x2A, 0x2A, 0x31));
    private static readonly Brush BtnBg = new SolidColorBrush(SwmColor.FromRgb(0x23, 0x23, 0x2A));
    private static readonly Brush Divider = new SolidColorBrush(SwmColor.FromRgb(0x39, 0x3E, 0x45));

    private static readonly int[] Modes = { 15, 25, 30, 45, 60 };

    private readonly PomodoroService _svc = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // ---- 圆环 ----
    private readonly Canvas _ringCanvas = new() { Width = 220, Height = 220, ClipToBounds = true };
    private System.Windows.Shapes.Path? _arcPath; // 独立引用管理弧（绝不按索引删子元素）

    private readonly TextBlock _timeText = new()
    {
        FontSize = 36, FontWeight = FontWeights.Light,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Foreground = White, TextAlignment = TextAlignment.Center, Width = 220,
    };
    private readonly TextBlock _phaseText = new()
    {
        FontSize = 12, Foreground = SubText, TextAlignment = TextAlignment.Center, Width = 220,
    };

    private readonly Dictionary<int, Button> _modeBtns = new();
    private Button _btnStart = null!, _btnPause = null!, _btnReset = null!;
    private Button _hardToggle = null!;
    private Border _hardTrack = null!, _hardThumb = null!;
    private bool _lastFinished; // 完成蜂鸣只响一次

    /// <summary>用户想要的硬性专注状态（预览模式下 HardFocus.Active 不置位，视觉以此为准）。</summary>
    private bool _hardWanted;
    /// <summary>预览模式（硬性专注仅演示不禁真锁）。</summary>
    private bool _previewMode;

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

    private void OnSvcPhaseChanged() => Dispatcher.Invoke(() =>
    {
        // 硬性专注唯一出口：专注段跑完进入休息 → 自动关（用户 2026-08-31 指示）
        if ((_hardWanted || HardFocus.Active) &&
            _svc.CurrentPhase is PomodoroService.Phase.ShortBreak or PomodoroService.Phase.LongBreak)
        {
            _hardWanted = false;
            HardFocus.Release();
        }
        Redraw();
    });

    public void LoadConfig(AppSettings cfg)
    {
        _svc.LoadConfig(cfg);
        Redraw();
    }

    public void SetPreviewMode(bool preview)
    {
        if (_previewMode == preview) return;

        // 首次向导预览期间硬性专注只画 UI，不应把状态带入正式运行态。
        if (!preview && _hardWanted)
        {
            HardFocus.Release();
            _hardWanted = false;
            _svc.Reset();
            _lastFinished = false;
        }
        _previewMode = preview;
        Redraw();
    }

    // ---------------- UI 构建 ----------------

    private void BuildUi()
    {
        Background = Brushes.Transparent;
        Width = 360;

        var root = new StackPanel();

        // ── 顶部：硬性专注滑块开关（居中）──
        root.Children.Add(BuildHardToggle());

        // ── 圆环 ──
        _ringCanvas.HorizontalAlignment = HorizontalAlignment.Center;
        _ringCanvas.Margin = new Thickness(0, 4, 0, 8);

        var track = new Ellipse { Stroke = Track, StrokeThickness = 9, Width = 196, Height = 196 };
        System.Windows.Controls.Canvas.SetLeft(track, 12);
        System.Windows.Controls.Canvas.SetTop(track, 12);
        _ringCanvas.Children.Add(track);

        System.Windows.Controls.Canvas.SetLeft(_timeText, 0);
        System.Windows.Controls.Canvas.SetTop(_timeText, 78);
        _ringCanvas.Children.Add(_timeText);
        System.Windows.Controls.Canvas.SetLeft(_phaseText, 0);
        System.Windows.Controls.Canvas.SetTop(_phaseText, 124);
        _ringCanvas.Children.Add(_phaseText);

        root.Children.Add(_ringCanvas);

        // ── 时长分段选择 ──
        root.Children.Add(BuildModeSelector());

        // ── 操作按钮 ──
        var actRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _btnStart = MkPill("开始", (_, _) => Start(), Teal, dark: true);
        _btnPause = MkPill("暂停", (_, _) => Pause(), BtnBg, dark: false);
        _btnReset = MkPill("重置", (_, _) => Reset(), BtnBg, dark: false);
        actRow.Children.Add(_btnStart);
        actRow.Children.Add(_btnPause);
        actRow.Children.Add(_btnReset);
        root.Children.Add(actRow);

        Content = root;
    }

    /// <summary>硬性专注行：标签 + iOS 式滑块开关。开始前可开关；开始后锁死；跑完自动关。</summary>
    private UIElement BuildHardToggle()
    {
        _hardToggle = new Button
        {
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // 最小模板（仅 ContentPresenter）：防默认 chrome 通栏浅蓝渐变
        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        _hardToggle.Template = new System.Windows.Controls.ControlTemplate(typeof(Button))
        {
            VisualTree = cpFactory,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock
        {
            Text = "硬性专注",
            FontSize = 12.5,
            Foreground = SubText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        // 滑块开关：轨道 40x22 + 白色圆头 18x18，位置由 Redraw 按状态设置
        _hardTrack = new Border
        {
            Width = 40,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(SwmColor.FromRgb(0x3A, 0x3F, 0x46)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _hardThumb = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = White,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _hardTrack.Child = _hardThumb;
        row.Children.Add(label);
        row.Children.Add(_hardTrack);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        _hardToggle.Content = row;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_hardToggle, "HardFocusToggle");
        _hardToggle.Click += (_, _) => ToggleHardFocus();
        return WrapCentered(_hardToggle);
    }

    /// <summary>时长分段选择：5 个等宽按钮（Width=60 固定、Margin 2 匀距）横排，叠加层丢弃——
    /// 激活态直接画在按钮 Background 上（TemplateBinding 到模板 Border）。固定宽度保证
    /// 高亮块永远完整矩形、间距绝对均匀；最小模板保证禁用态=原样变暗、无默认 chrome。
    /// 上一版 Grid-Star 列在无外部宽度约束时退化为内容宽度→数字挤作一团、高亮变圆形浮块
    ///（用户 14:52 截图实锤），此版从根上消除。</summary>
    private UIElement BuildModeSelector()
    {
        var host = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = BtnBg,
            Padding = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var m in Modes)
        {
            var b = new Button
            {
                Content = $"{m}",
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Width = 60,                                   // 固定等宽：单元格+高亮块一致
                Padding = new Thickness(0, 7, 0, 7),
                Margin = new Thickness(2, 0, 2, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(SwmColor.FromRgb(0xCF, 0xCF, 0xCF)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                ToolTip = $"{m} 分钟专注",
            };
            // 最小模板：单 Border 圆角 7 + ContentPresenter；激活时 Border 整体青绿。
            // Padding 直接写在模板 Border 上。垂直 Padding 8 + 容器 Padding 1 = 9，
            // 与 MkPill 的垂直 Padding 9 恒等 → 总高精确一致（用户 14:58 截图对比实锤）。
            var modeBorder = new FrameworkElementFactory(typeof(Border));
            modeBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            modeBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Border.BackgroundProperty));
            modeBorder.SetValue(Border.PaddingProperty, new Thickness(0, 8, 0, 8));
            var modeCp = new FrameworkElementFactory(typeof(ContentPresenter));
            modeCp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            modeCp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            modeBorder.AppendChild(modeCp);
            b.Template = new System.Windows.Controls.ControlTemplate(typeof(Button))
            {
                VisualTree = modeBorder,
            };
            b.Click += (_, _) => SetMode(m);
            _modeBtns[m] = b;
            sp.Children.Add(b);
        }
        host.Child = sp;
        return WrapCentered(host);
    }

    private static StackPanel WrapCentered(UIElement el)
    {
        var g = new System.Windows.Controls.Grid();
        g.Children.Add(el);
        var sp = new StackPanel();
        sp.Children.Add(g);
        return sp;
    }

    private Button MkPill(string text, RoutedEventHandler onClick, Brush bg, bool dark)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(5, 0, 5, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = bg,
            Foreground = dark ? Brushes.Black : White,
            Focusable = false,
        };
        // 圆角药丸模板：内边距直接设在模板 Border 上（代码构建模板下 TemplateBinding 转发 Padding 不生效）
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Border.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Border.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Border.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new Thickness(22, 9, 22, 9));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        border.AppendChild(cp);
        b.Template = new System.Windows.Controls.ControlTemplate(typeof(Button))
        {
            VisualTree = border,
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
        Redraw();
    }

    private void Start() { _svc.Start(); Redraw(); }

    /// <summary>硬性专注开关：开始前可开关；开启即拨 30 分钟；工作段进行中不可取消；跑完自动关。</summary>
    private void ToggleHardFocus()
    {
        // 已开启且已开始（Work 进行中）：不可取消
        if (_hardWanted && _svc.CurrentPhase == PomodoroService.Phase.Work) return;
        if (_hardWanted)
        {
            // 开始前取消：回 Idle、关闭系统锁、时长保持当前值
            _hardWanted = false;
            HardFocus.Release();
            _svc.Reset();
            Redraw();
            return;
        }
        // 开启：时长拨到 30，不预启动（用户按 开始 才跑）
        _svc.WorkMinutes = 30;
        _svc.Reset();
        _lastFinished = false;
        _hardWanted = true;
        HardFocus.Enable(!_previewMode);
        Redraw();
    }

    /// <summary>硬性专注工作段进行中：时长选择与重置全部锁定。预览模式同样生效（可见即可感）。</summary>
    private bool HardLocked => _hardWanted &&
        _svc.CurrentPhase == PomodoroService.Phase.Work;

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
            ? FmtDbl(TimeSpan.FromMinutes(_svc.WorkMinutes))
            : FmtDbl(remain);

        // 阶段小字（环内、时间下方）
        _phaseText.Text = _svc.CurrentPhase switch
        {
            PomodoroService.Phase.Idle => "准备",
            PomodoroService.Phase.Work => "专注",
            PomodoroService.Phase.ShortBreak => "小憩",
            PomodoroService.Phase.LongBreak => "长憩",
            _ => "",
        };

        // 按钮状态机：Idle=[开始] Running=[暂停+重置] Paused=[继续+重置]
        var idle = _svc.CurrentPhase == PomodoroService.Phase.Idle;
        var running = _svc.IsRunning;
        _btnStart.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        _btnPause.Content = running ? "暂停" : "继续";
        _btnPause.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        _btnReset.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;

        // 硬性专注：iOS 式开关视觉（开=金轨道+滑块右移）；重置在工作段进行中禁用
        if (_hardWanted)
        {
            _btnReset.IsEnabled = !HardLocked;
            _btnReset.Opacity = HardLocked ? 0.45 : 1;
            _hardTrack.Background = Gold;
            _hardThumb.HorizontalAlignment = HorizontalAlignment.Right;
            _hardThumb.Margin = new Thickness(0, 0, 2, 0);
        }
        else
        {
            _btnReset.IsEnabled = true;
            _btnReset.Opacity = 1;
            _hardTrack.Background = new SolidColorBrush(SwmColor.FromRgb(0x3A, 0x3F, 0x46));
            _hardThumb.HorizontalAlignment = HorizontalAlignment.Left;
            _hardThumb.Margin = new Thickness(2, 0, 0, 0);
        }
        // 硬性专注工作段进行中：时长选择整组禁点（开始后不可更改）
        foreach (var (_, b) in _modeBtns)
        {
            b.IsEnabled = !HardLocked;
            b.Opacity = HardLocked ? 0.45 : 1;
        }

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

    /// <summary>分:秒格式化（总分钟不取模——60 分钟显示 60:00 而非 00:00）。
    /// TimeSpan 的 @"mm\:ss" 中 mm 是「分钟位」（0–59），60 分钟进位成 1 小时后分钟位回 0，
    /// 用户 15:01 截图实锤「选 60 显示 00:00」。</summary>
    private static string FmtDbl(TimeSpan t)
    {
        var total = (int)Math.Floor(t.TotalMinutes);
        return $"{total:00}:{t.Seconds:00}";
    }

    /// <summary>进度弧：从 12 点钟起顺时针扫 ratio 比例。
    /// 终点数学：顺时针方向向量 = (sin θ, −cos θ)，θ=ratio·360°（v0.5.3 修正）。
    /// 防退化阈值 0.995（0.985 会让开跑后 ~20 秒整圆跳缺口，用户感知为「混乱」）。</summary>
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

        if (ratio > 0.995)
        {
            // 仅防真退化（起点=终点的未定义弧）
            var full = new Path
            {
                Stroke = ArcColor(), StrokeThickness = 9,
                Data = new EllipseGeometry(new System.Windows.Point(110, 110), 98, 98),
            };
            _arcPath = full;
            _ringCanvas.Children.Add(full);
            return;
        }
        if (ratio <= 0.002) return;

        var color = ArcColor();
        var cx = 110.0; var cy = 110.0; var R = 98.0;
        var theta = ratio * 360.0;                 // 顺时针角（从 12 点钟起）
        var rad = theta * Math.PI / 180.0;
        var start = new System.Windows.Point(cx, cy - R);
        var end = new System.Windows.Point(cx + R * Math.Sin(rad), cy - R * Math.Cos(rad));
        var large = ratio > 0.5;

        var arc = new Path
        {
            Stroke = color,
            StrokeThickness = 9,
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

    private Brush ArcColor() => !_svc.IsRunning ? Blue
        : _svc.CurrentPhase == PomodoroService.Phase.Work ? TealHover
        : Gold;

    private void StyleModeButtons()
    {
        foreach (var (m, b) in _modeBtns)
        {
            var active = m == _svc.WorkMinutes;
            b.Background = active ? Teal : Brushes.Transparent;
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
