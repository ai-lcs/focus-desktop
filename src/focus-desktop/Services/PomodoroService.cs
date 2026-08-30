using System.IO;

namespace focus_desktop.Services;

/// <summary>
/// 番茄钟（Focuser pomodoro.rs 状态机模型的 C# 移植，高度自定义）：
/// - 三阶段循环：专注(work) → 小憩(short break) → 每 N 轮长休(long break)
/// - 自定义：work/short/long 时长（分钟）+ long 间隔轮数，config.json 可改
/// - 状态保存在内存 + 持久化到 session（崩溃重启不丢当轮进度：见 Persist/Restore）
/// - 暂停 = 冻结剩余秒数（paused_remaining_secs 模型），恢复时回拨 start 时间戳
/// </summary>
public sealed class PomodoroService
{
    // ---- 配置（高度自定义） ----
    public int WorkMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int CyclesUntilLong { get; set; } = 4;

    // ---- 运行状态（Focuser 模型） ----
    public enum Phase { Idle, Work, ShortBreak, LongBreak }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public int CompletedCycles { get; private set; }

    private DateTime _phaseStart;
    private TimeSpan? _pausedRemaining;   // 暂停时冻结的剩余时长
    private bool _running;

    public bool IsRunning => _running;
    public int CurrentPhaseMinutes => CurrentPhase switch
    {
        Phase.Work => WorkMinutes,
        Phase.ShortBreak => ShortBreakMinutes,
        Phase.LongBreak => LongBreakMinutes,
        _ => 0,
    };

    public event Action? PhaseChanged;
    public event Action? Tick;

    private readonly System.Windows.Threading.DispatcherTimer _timer =
        new() { Interval = TimeSpan.FromSeconds(1) };

    public PomodoroService()
    {
        _timer.Tick += (_, _) => Tick?.Invoke();
    }

    public void LoadConfig(AppSettings cfg)
    {
        if (cfg.PomodoroWorkMinutes is int w and > 0) WorkMinutes = w;
        if (cfg.PomodoroShortBreakMinutes is int s and > 0) ShortBreakMinutes = s;
        if (cfg.PomodoroLongBreakMinutes is int l and > 0) LongBreakMinutes = l;
        if (cfg.PomodoroCyclesUntilLong is int c and > 0) CyclesUntilLong = c;
    }

    // ---- 控制 ----

    public void Start()
    {
        if (CurrentPhase == Phase.Idle)
            BeginPhase(Phase.Work);
        else
            Resume();
        _timer.Start();
    }

    public void Pause()
    {
        if (!_running) return;
        _pausedRemaining = Remaining();
        _running = false;
        _timer.Stop();
    }

    public void Resume()
    {
        if (_running || CurrentPhase == Phase.Idle) return;
        // 冻结剩余 → 回拨 start：start = now - (duration - remaining)
        var remain = _pausedRemaining ?? Remaining();
        _phaseStart = DateTime.Now - (TimeSpan.FromMinutes(CurrentPhaseMinutes) - remain);
        _pausedRemaining = null;
        _running = true;
        _timer.Start();
    }

    public void Reset()
    {
        CurrentPhase = Phase.Idle;
        CompletedCycles = 0;
        _running = false;
        _pausedRemaining = null;
        _timer.Stop();
        PhaseChanged?.Invoke();
    }

    public void Skip() // 跳到下一阶段
    {
        if (CurrentPhase == Phase.Idle) { BeginPhase(Phase.Work); return; }
        AdvancePhase();
    }

    // ---- 状态查询 ----

    /// <summary>当前阶段剩余时长。</summary>
    public TimeSpan Remaining()
    {
        if (CurrentPhase == Phase.Idle) return TimeSpan.Zero;
        if (_pausedRemaining != null) return _pausedRemaining.Value;
        var elapsed = DateTime.Now - _phaseStart;
        var total = TimeSpan.FromMinutes(CurrentPhaseMinutes);
        var remain = total - elapsed;
        return remain > TimeSpan.Zero ? remain : TimeSpan.Zero;
    }

    // ---- 内部 ----

    private void BeginPhase(Phase p)
    {
        CurrentPhase = p;
        _phaseStart = DateTime.Now;
        _pausedRemaining = null;
        _running = true;
        PhaseChanged?.Invoke();
    }

    /// <summary>每秒 tick 由 UI 调用：检查阶段是否结束。</summary>
    public void OnSecond()
    {
        if (!_running) return;
        if (Remaining() <= TimeSpan.Zero)
            AdvancePhase();
    }

    private void AdvancePhase()
    {
        switch (CurrentPhase)
        {
            case Phase.Work:
                CompletedCycles++;
                if (CompletedCycles % CyclesUntilLong == 0)
                    BeginPhase(Phase.LongBreak);
                else
                    BeginPhase(Phase.ShortBreak);
                break;
            case Phase.ShortBreak:
            case Phase.LongBreak:
                BeginPhase(Phase.Work);
                break;
        }
    }
}
