p = r'D:/focus-desktop/src/focus-desktop/PomodoroControl.cs'
src = open(p, encoding='utf-8').read()

# 1) 环中心：加阶段标签
old = '''        Canvas.SetLeft(_timeText, 0); Canvas.SetTop(_timeText, 54);
        _ringCanvas.Children.Add(_timeText);
        Canvas.SetLeft(_statusText, 0); Canvas.SetTop(_statusText, 92);
        _ringCanvas.Children.Add(_statusText);'''
new = '''        _phaseLabel = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Width = 150, Foreground = Muted };
        Canvas.SetLeft(_phaseLabel, 0); Canvas.SetTop(_phaseLabel, 44);
        _ringCanvas.Children.Add(_phaseLabel);
        Canvas.SetLeft(_timeText, 0); Canvas.SetTop(_timeText, 62);
        _ringCanvas.Children.Add(_timeText);
        Canvas.SetLeft(_statusText, 0); Canvas.SetTop(_statusText, 96);
        _ringCanvas.Children.Add(_statusText);'''
assert old in src, "ring text layout not found"
src = src.replace(old, new)

# 字段声明
old = '''    private readonly TextBlock _statusText = new()
    {
        FontSize = 11, TextAlignment = TextAlignment.Center, Width = 150,
    };'''
new = '''    private readonly TextBlock _statusText = new()
    {
        FontSize = 11, TextAlignment = TextAlignment.Center, Width = 150,
    };
    private TextBlock _phaseLabel = null!;'''
assert old in src
src = src.replace(old, new)

# 2) Redraw：阶段标签 + 阶段变色 + idle 规则脚注
old = '''        _cycleText.Text = $"第 {_svc.CompletedCycles} 轮";

        string status; Brush statusColor;
        switch (_svc.CurrentPhase)
        {
            case PomodoroService.Phase.Idle:
                status = $"{_svc.WorkMinutes} 分钟专注模式"; statusColor = Muted; break;
            case PomodoroService.Phase.Work when _svc.IsRunning:
                status = "专注中"; statusColor = TealHover; break;
            case PomodoroService.Phase.Work:
                status = "已暂停"; statusColor = Blue; break;
            case PomodoroService.Phase.ShortBreak when _svc.IsRunning:
            case PomodoroService.Phase.LongBreak when _svc.IsRunning:
                status = "休息中"; statusColor = Teal; break;
            default:
                status = "休息暂停"; statusColor = Blue; break;
        }
        _statusText.Text = _svc.CompletedCycles > 0 ? $"第 {_svc.CompletedCycles} 轮 · {status}" : status;
        _statusText.Foreground = statusColor;'''
new = '''        // 阶段标签（环内顶部）：Idle=番茄钟 / Work=专注 / Break=休息 —— 一眼看出在哪个阶段
        var (phaseName, phaseBrush) = _svc.CurrentPhase switch
        {
            PomodoroService.Phase.Idle => ("番茄钟", Muted),
            PomodoroService.Phase.Work => ("专注", TealHover),
            _ => ("休息", Gold),
        };
        var paused = _svc.CurrentPhase != PomodoroService.Phase.Idle && !_svc.IsRunning;
        _phaseLabel.Text = paused ? $"{phaseName}·已暂停" : phaseName;
        _phaseLabel.Foreground = paused ? Blue : phaseBrush;

        // 状态行（环内底部）：idle 显示规则脚注（自我解释），运行时显示轮数+下一步
        string status;
        if (_svc.CurrentPhase == PomodoroService.Phase.Idle)
            status = $"{_svc.WorkMinutes} 分钟 · 每 {_svc.CyclesUntilLong} 轮长休 {_svc.LongBreakMinutes} 分";
        else if (_svc.CompletedCycles > 0)
            status = $"第 {_svc.CompletedCycles} 轮 · 下一步：{(_svc.CurrentPhase == PomodoroService.Phase.Work ? "休息" : "专注")}";
        else
            status = $"下一步：休息";
        _statusText.Text = status;
        _statusText.Foreground = paused ? Blue : Muted;'''
assert old in src, "redraw status block not found"
src = src.replace(old, new)

# 3) 按钮状态机
old = '''        // 操作按钮文案/状态
        _btnStart.Content = _svc.CurrentPhase == PomodoroService.Phase.Idle ? "开始" : (_svc.IsRunning ? "…" : "开始");
        _btnStart.IsEnabled = !_svc.IsRunning;
        _btnPause.Content = _svc.IsRunning ? "暂停" : "继续";
        StyleModeButtons();
        StyleActionButtons();'''
new = '''        // 按钮状态机：Idle=[开始] Running=[暂停+重置] Paused=[继续+重置]
        var idle = _svc.CurrentPhase == PomodoroService.Phase.Idle;
        var running = _svc.IsRunning;
        _btnStart.Content = idle ? "开始" : "…";
        _btnStart.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        _btnPause.Content = running ? "暂停" : "继续";
        _btnPause.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        _btnReset.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        StyleModeButtons();
        StyleActionButtons();'''
assert old in src, "button state machine not found"
src = src.replace(old, new)

# 4) DrawArc 阶段变色
old = '''        var color = _svc.IsRunning
            ? (_svc.CurrentPhase == PomodoroService.Phase.Work ? TealHover : Teal)
            : Blue;'''
new = '''        var color = !_svc.IsRunning ? Blue
            : _svc.CurrentPhase == PomodoroService.Phase.Work ? TealHover
            : Gold;'''
assert old in src, "arc color not found"
src = src.replace(old, new)

# 5) StyleActionButtons 重写
import re
m = re.search(r'    private void StyleActionButtons\(\)\n    \{.*?\n    \}', src, re.S)
assert m, "StyleActionButtons not found"
new_fn = '''    private void StyleActionButtons()
    {
        // 主键（青绿高亮）：Idle=开始；Running=暂停为次键；Paused=继续当主键
        var idle = _svc.CurrentPhase == PomodoroService.Phase.Idle;
        var running = _svc.IsRunning;
        _btnStart.Background = Teal;
        _btnStart.Foreground = Brushes.Black;
        if (idle || running)
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
    }'''
src = src.replace(m.group(0), new_fn)

open(p, 'w', encoding='utf-8').write(src)
print("PomodoroControl redesigned: phase label + phase colors + button state machine")
