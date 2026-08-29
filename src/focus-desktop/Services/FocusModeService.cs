namespace focus_desktop.Services;

/// <summary>
/// 焦点模式编排（spec §4 的 EnterFocusMode / ExitFocusMode / RecoverWindowsState）。
/// 恢复代码只有一份（Recover/Exit 共用内核），任何死法都汇到这。
/// 顺序纪律：Enter 先写脏标志再做任何系统修改；Exit 先拆系统修改最后清标志。
/// 这样中间任何一步崩，脏标志都残留 → 下次启动自愈。
/// </summary>
public sealed class FocusModeService
{
    private readonly KeyboardGuardService _keyboard = new();

    public bool IsActive { get; private set; }

    /// <summary>进入锁定：脏标志 → 隐藏任务栏 → 挂钩子。</summary>
    public void Enter()
    {
        if (IsActive) return;

        RecoveryService.MarkActive(); // 1. 先立牌：从这一刻起崩了下次要自愈

        TaskbarService.Hide();        // 2. 藏任务栏

        _keyboard.Install();          // 3. 挂键盘钩子（最后挂：钩子要在 UI 线程消息循环里活着）

        IsActive = true;
    }

    /// <summary>正常退出：钩子 → 任务栏 → 清标志。完全倒序。</summary>
    public void Exit()
    {
        if (!IsActive) return;
        IsActive = false;

        _keyboard.Uninstall();        // 1. 先放键盘——最影响用户的一层

        var shown = TaskbarService.Show(); // 2. 恢复任务栏（含 explorer 兜底）

        RecoveryService.MarkClean();  // 3. 最后清牌

        if (!shown)
        {
            CrashReporter.Write(
                new InvalidOperationException("退出时任务栏恢复后仍不可见（explorer 兜底也未确认），已写日志。可用 --restore 手动恢复"),
                "focus-exit-taskbar-uncertain");
        }
    }

    /// <summary>异常/未知状态恢复：吞一切异常，尽力而为。幂等——重复调用无害。</summary>
    public void Recover()
    {
        try { _keyboard.Uninstall(); } catch { }
        try { TaskbarService.Show(); } catch { }
        try { RecoveryService.MarkClean(); } catch { }
        IsActive = false;
    }

    /// <summary>OnExit 调用：激活过才走完整退出流程。</summary>
    public void ExitIfActive()
    {
        if (IsActive) Exit();
        else Recover(); // 没激活过也可能残留系统状态（如 Enter 中途崩）——顺手幂等恢复
    }
}
