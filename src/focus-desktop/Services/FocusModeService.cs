namespace focus_desktop.Services;

/// <summary>Coordinates focus lock, Dock lifetime, and Windows recovery.</summary>
public sealed class FocusModeService
{
    private readonly KeyboardGuardService _keyboard = new();
    private MainWindow? _mainWindow;
    private IReadOnlyList<ExternalAppEntry> _dockEntries = Array.Empty<ExternalAppEntry>();
    private DockWindow? _dock;
    private bool _registerAppBarWhenActive;
    private bool _dockReleaseConfirmed = true;
    private bool _sessionStartedByThisInstance;

    public bool IsActive { get; private set; }
    public bool HasRecoveryResponsibility => IsActive || _dock != null || RecoveryService.WasActiveLastTime();
    public bool IsExternalWindowForeground => _dock?.IsExternalWindowForeground == true;

    public void ConfigureDock(MainWindow mainWindow, IEnumerable<ExternalAppEntry> entries, bool registerAppBarWhenActive)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(entries);
        _mainWindow = mainWindow;
        _dockEntries = entries.OrderBy(entry => entry.Order).ToArray();
        _registerAppBarWhenActive = registerAppBarWhenActive;
        if (_dockEntries.Count == 0) ReleaseDock();
    }

    public void RefreshDockSettings(IEnumerable<ExternalAppEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _dockEntries = entries.OrderBy(entry => entry.Order).ToArray();
        if (_dockEntries.Count == 0)
        {
            ReleaseDock();
            return;
        }

        if (IsActive && !StartDock(_registerAppBarWhenActive)) Recover();
    }

    public bool ShowDevelopmentDock(bool registerAppBar)
    {
        if (StartDock(registerAppBar)) return true;
        Recover();
        _mainWindow?.ReturnToFocusDesk();
        return false;
    }

    internal bool TryGetAppBarLayout(out AppBarLayout layout)
    {
        if (_dock != null && _dock.TryGetAppBarLayout(out layout)) return true;
        layout = default;
        return false;
    }

    /// <summary>Enters focus mode: preserve recovery marker until every owned system change is released.</summary>
    public void Enter()
    {
        if (IsActive) return;
        try
        {
            MonitorWorkAreaSnapshot? originalMonitor = null;
            if (_registerAppBarWhenActive)
            {
                if (_mainWindow == null || !AppBarWindowService.TryCaptureMonitorSnapshot(_mainWindow, out var snapshot))
                    throw new InvalidOperationException("无法读取 Dock 显示器的原工作区，已取消专注模式。");
                originalMonitor = snapshot;
            }

            RecoveryService.MarkActive(originalMonitor);
            _sessionStartedByThisInstance = true;
            WatchdogService.Launch();
            TaskbarService.Hide();
            _keyboard.Install();
            if (!StartDock(_registerAppBarWhenActive))
                throw new InvalidOperationException("Dock 无法注册为 Windows 桌面栏，已回滚专注模式。");
            IsActive = true;
        }
        catch
        {
            Recover();
            throw;
        }
    }

    public void Exit()
    {
        if (!IsActive && _dock == null) return;
        IsActive = false;
        _keyboard.Uninstall();
        var dockReleased = ReleaseDock();
        var taskbarRestored = TaskbarService.Show();
        if (dockReleased && taskbarRestored && RecoveryService.WasActiveLastTime())
            dockReleased = RecoveryService.TryConfirmOwnedAppBarReleased(out _);
        CompleteRecovery(taskbarRestored, dockReleased, "focus-exit-recovery-uncertain");
    }

    public void Recover()
    {
        IsActive = false;
        try { _keyboard.Uninstall(); } catch { }
        var hasOwnedDock = _dock != null;
        var dockReleased = hasOwnedDock ? ReleaseDock() : _dockReleaseConfirmed;
        var taskbarRestored = false;
        try { taskbarRestored = TaskbarService.Show(); } catch { }
        if (taskbarRestored && RecoveryService.WasActiveLastTime())
        {
            if (_sessionStartedByThisInstance || hasOwnedDock)
            {
                // This instance owns the marker and has closed its Dock. Verify registered AppBar
                // geometry after Taskbar.Show; an initialization failure before registration is
                // covered by the local no-AppBar state.
                var locallyReleased = hasOwnedDock ? dockReleased : _dockReleaseConfirmed;
                dockReleased = locallyReleased && RecoveryService.TryConfirmOwnedAppBarReleased(out _);
            }
            else if (!hasOwnedDock)
            {
                try { dockReleased = RecoveryService.TryConfirmAppBarReleased(null, null, out _); } catch { }
            }
        }
        try { CompleteRecovery(taskbarRestored, dockReleased, "focus-recovery-uncertain"); } catch { }
    }

    public void ExitIfActive()
    {
        if (IsActive || _dock != null) Exit();
        else Recover();
    }

    public static bool CanMarkRecoveryClean(bool taskbarRestored, bool dockReleased) =>
        taskbarRestored && dockReleased;

    private bool StartDock(bool registerAppBar)
    {
        if (_dockEntries.Count == 0) return true;
        if (_dock != null) return !_dock.IsClosed;
        if (_mainWindow == null) return false;

        try
        {
            if (registerAppBar) _mainWindow.PrepareForAppBarDock();
            var dock = new DockWindow(_mainWindow, this, _dockEntries, _keyboard);
            _dock = dock;
            _dockReleaseConfirmed = false;
            if (!dock.TryShowDock(registerAppBar)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ReleaseDock()
    {
        var dock = _dock;
        if (dock == null) return _dockReleaseConfirmed;

        bool appBarReleased;
        try { appBarReleased = dock.ReleaseAppBar(); } catch { appBarReleased = false; }
        if (!appBarReleased) return false;

        if (!dock.IsClosed)
        {
            try { dock.Close(); } catch { }
        }

        _dockReleaseConfirmed = dock.IsClosed && dock.IsAppBarReleased;
        if (_dockReleaseConfirmed)
        {
            _dock = null;
        }
        return _dockReleaseConfirmed;
    }

    private void CompleteRecovery(bool taskbarRestored, bool dockReleased, string source)
    {
        if (CanMarkRecoveryClean(taskbarRestored, dockReleased))
        {
            RecoveryService.MarkClean();
            _sessionStartedByThisInstance = false;
            WatchdogService.Stop();
            return;
        }

        CrashReporter.Write(new InvalidOperationException(
            $"恢复状态未确认：任务栏={taskbarRestored}，Dock/AppBar={dockReleased}；保留恢复标记。"), source);
    }
}
