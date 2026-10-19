using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace focus_desktop.Services;

/// <summary>Persists the resources that a later process must verify before clearing recovery state.</summary>
public static class RecoveryService
{
    private sealed record SessionState(
        [property: JsonPropertyName("focus_mode_active")] bool FocusModeActive,
        [property: JsonPropertyName("appbar_expected")] bool? AppBarExpected = null,
        [property: JsonPropertyName("appbar_hwnd")] long? AppBarHwnd = null,
        [property: JsonPropertyName("parent_pid")] int? ParentPid = null,
        [property: JsonPropertyName("parent_start_ticks")] long? ParentStartTicks = null,
        [property: JsonPropertyName("monitor_device")] string? MonitorDevice = null,
        [property: JsonPropertyName("monitor_left")] int? MonitorLeft = null,
        [property: JsonPropertyName("monitor_top")] int? MonitorTop = null,
        [property: JsonPropertyName("monitor_right")] int? MonitorRight = null,
        [property: JsonPropertyName("monitor_bottom")] int? MonitorBottom = null,
        [property: JsonPropertyName("work_left")] int? WorkLeft = null,
        [property: JsonPropertyName("work_top")] int? WorkTop = null,
        [property: JsonPropertyName("work_right")] int? WorkRight = null,
        [property: JsonPropertyName("work_bottom")] int? WorkBottom = null);

    internal static void MarkActive(MonitorWorkAreaSnapshot? originalMonitor = null)
    {
        if (WasActiveLastTime())
            throw new InvalidOperationException("上次会话的恢复状态仍未确认，不能覆盖原恢复记录。");

        TryGetCurrentProcessIdentity(out var processId, out var startTicks);
        Paths.EnsureDirectories();
        AtomicFile.WriteAllText(Paths.SessionStateFile,
            JsonSerializer.Serialize(CreateState(true, appBarExpected: false,
                processId: processId == 0 ? null : processId,
                startTicks: startTicks == 0 ? null : startTicks,
                originalMonitor: originalMonitor)));
    }

    internal static bool TryRecordAppBarCandidate(IntPtr hwnd, MonitorWorkAreaSnapshot currentMonitor)
    {
        try
        {
            var state = ReadState();
            if (state?.FocusModeActive == true)
            {
                if (state.AppBarExpected == true
                    || !TryGetCurrentProcessIdentity(out var currentPid, out var currentStartTicks)
                    || state.ParentPid != currentPid || state.ParentStartTicks != currentStartTicks)
                    return false;
            }
            else if (state == null && WasActiveLastTime())
            {
                return false;
            }

            var originalMonitor = state is { FocusModeActive: true }
                ? ReadSnapshot(state)
                : currentMonitor;
            if (originalMonitor is not MonitorWorkAreaSnapshot monitorSnapshot) return false;
            if (!SameMonitor(monitorSnapshot, currentMonitor)
                || !TryGetCurrentProcessIdentity(out var processId, out var startTicks))
                return false;

            Paths.EnsureDirectories();
            AtomicFile.WriteAllText(Paths.SessionStateFile,
                JsonSerializer.Serialize(CreateState(
                    focusModeActive: true,
                    appBarExpected: true,
                    hwnd,
                    processId,
                    startTicks,
                    monitorSnapshot)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void MarkClean()
    {
        Paths.EnsureDirectories();
        AtomicFile.WriteAllText(Paths.SessionStateFile,
            JsonSerializer.Serialize(CreateState(false, appBarExpected: false)));
    }

    public static bool WasActiveLastTime()
    {
        try
        {
            using var stream = new FileStream(Paths.SessionStateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("focus_mode_active", out var active)
                && active.ValueKind == JsonValueKind.True;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch
        {
            // 状态损坏/不可读时保守恢复；不能据此清除恢复标志。
            return true;
        }
    }

    /// <summary>Legacy marker files have no AppBar fields and retain their taskbar-only recovery behavior.</summary>
    public static bool TryConfirmRecovery(bool taskbarRestored, int? expectedParentPid,
        long? expectedParentStartTicks, out string reason)
    {
        if (!taskbarRestored)
        {
            reason = "任务栏恢复未确认";
            return false;
        }

        if (!TryConfirmAppBarReleased(expectedParentPid, expectedParentStartTicks, out reason))
            return false;

        reason = "任务栏与 AppBar 恢复均已确认";
        return true;
    }

    public static bool TryConfirmAppBarReleased(int? expectedParentPid, long? expectedParentStartTicks,
        out string reason)
    {
        if (!WasActiveLastTime())
        {
            reason = "没有待恢复会话";
            return true;
        }

        var state = ReadState();
        if (state == null)
        {
            reason = "恢复记录不可读";
            return false;
        }

        if (expectedParentPid.HasValue && state.ParentPid.HasValue
            && expectedParentPid.Value != state.ParentPid.Value
            || expectedParentStartTicks.HasValue && state.ParentStartTicks.HasValue
            && expectedParentStartTicks.Value != state.ParentStartTicks.Value)
        {
            reason = "看门狗身份与恢复记录不匹配";
            return false;
        }

        var hasNewRecoveryMetadata = HasNewRecoveryMetadata(state);
        if (state.AppBarExpected is null
            && !CanUseLegacyTaskbarRecovery(state.AppBarExpected, hasNewRecoveryMetadata))
        {
            reason = "AppBar 恢复标志缺失但记录含有新版元数据，拒绝按旧版规则清理";
            return false;
        }

        // Missing AppBar fields means a legacy marker. Explicit false means this session did not register an AppBar.
        if (state.AppBarExpected != true)
        {
            if (state.AppBarExpected == false
                && !CanConfirmNoAppBarRecovery(state.AppBarHwnd.HasValue,
                    state.ParentPid is int && state.ParentStartTicks is long))
            {
                reason = "未注册 AppBar 的恢复记录包含冲突 HWND 或缺少主进程身份";
                return false;
            }

            if (state.ParentPid is int taskParentPid && state.ParentStartTicks is long taskParentStartTicks
                && !IsProcessIdentityExited(taskParentPid, taskParentStartTicks))
            {
                reason = "记录中的 Focus Desk 主进程仍在运行";
                return false;
            }

            reason = state.AppBarExpected == false
                ? "该会话未注册 AppBar，任务栏恢复已足够"
                : "旧版会话未记录 AppBar，按旧恢复规则处理";
            return true;
        }

        var snapshot = ReadSnapshot(state);
        if (snapshot is not MonitorWorkAreaSnapshot monitorSnapshot || state.AppBarHwnd is not long hwnd
            || state.ParentPid is not int parentPid || state.ParentStartTicks is not long parentStartTicks)
        {
            reason = "AppBar 恢复元数据不完整";
            return false;
        }

        var parentExited = IsProcessIdentityExited(parentPid, parentStartTicks);
        var hwndDestroyed = parentExited && AppBarWindowService.IsOriginalWindowDestroyed(
            new IntPtr(hwnd), parentPid, parentStartTicks);
        var workAreaMatches = AppBarWindowService.WorkAreaMatches(monitorSnapshot);
        if (!CanConfirmAppBarReleased(parentExited, hwndDestroyed, workAreaMatches))
        {
            reason = $"AppBar 尚未确认释放：父进程已退出={parentExited}，原窗口已销毁={hwndDestroyed}，工作区已还原={workAreaMatches}";
            return false;
        }

        reason = "原 AppBar 窗口已销毁且显示器工作区已恢复";
        return true;
    }

    /// <summary>Confirms resources released by this still-running owner after Dock.Close and Taskbar.Show.</summary>
    internal static bool TryConfirmOwnedAppBarReleased(out string reason)
    {
        if (!WasActiveLastTime())
        {
            reason = "没有待恢复会话";
            return true;
        }

        var state = ReadState();
        if (state == null || !TryGetCurrentProcessIdentity(out var processId, out var startTicks)
            || state.ParentPid != processId || state.ParentStartTicks != startTicks)
        {
            reason = "当前进程与恢复记录中的会话身份不匹配";
            return false;
        }

        if (state.AppBarExpected is null
            && !CanUseLegacyTaskbarRecovery(state.AppBarExpected, HasNewRecoveryMetadata(state)))
        {
            reason = "AppBar 恢复标志缺失但记录含有新版元数据，拒绝按旧版规则清理";
            return false;
        }

        if (state.AppBarExpected == false
            && !CanConfirmNoAppBarRecovery(state.AppBarHwnd.HasValue,
                state.ParentPid is int && state.ParentStartTicks is long))
        {
            reason = "未注册 AppBar 的恢复记录包含冲突 HWND 或缺少主进程身份";
            return false;
        }

        // The caller owns this session and has already confirmed the Dock close. No AppBar was recorded
        // when registration never began; registered AppBars still require native release evidence.
        if (state.AppBarExpected != true)
        {
            reason = "本进程已确认没有待释放的 AppBar";
            return true;
        }

        var snapshot = ReadSnapshot(state);
        if (snapshot is not MonitorWorkAreaSnapshot monitorSnapshot || state.AppBarHwnd is not long hwnd)
        {
            reason = "AppBar 恢复元数据不完整";
            return false;
        }

        var hwndDestroyed = AppBarWindowService.IsOriginalWindowDestroyed(
            new IntPtr(hwnd), processId, startTicks);
        var workAreaMatches = AppBarWindowService.WorkAreaMatches(monitorSnapshot);
        if (!CanConfirmOwnedAppBarReleased(true, hwndDestroyed, workAreaMatches))
        {
            reason = $"AppBar 尚未确认释放：原窗口已销毁={hwndDestroyed}，工作区已还原={workAreaMatches}";
            return false;
        }

        reason = "本进程原 AppBar 窗口已销毁且显示器工作区已恢复";
        return true;
    }

    internal static bool CanConfirmAppBarReleased(bool parentExited, bool hwndDestroyed,
        bool workAreaMatchesOriginal) => parentExited && hwndDestroyed && workAreaMatchesOriginal;

    internal static bool CanConfirmOwnedAppBarReleased(bool ownerMatches, bool hwndDestroyed,
        bool workAreaMatchesOriginal) => ownerMatches && hwndDestroyed && workAreaMatchesOriginal;

    internal static bool CanUseLegacyTaskbarRecovery(bool? appBarExpected, bool hasNewMetadata) =>
        appBarExpected is null && !hasNewMetadata;

    internal static bool CanConfirmNoAppBarRecovery(bool appBarHwndPresent, bool parentIdentityComplete) =>
        !appBarHwndPresent && parentIdentityComplete;

    private static SessionState? ReadState()
    {
        try
        {
            using var stream = new FileStream(Paths.SessionStateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<SessionState>(stream);
        }
        catch { return null; }
    }

    private static SessionState CreateState(bool focusModeActive, bool appBarExpected,
        IntPtr hwnd = default, int? processId = null, long? startTicks = null,
        MonitorWorkAreaSnapshot? originalMonitor = null)
    {
        var snapshot = originalMonitor;
        return new SessionState(
            focusModeActive,
            appBarExpected,
            hwnd == IntPtr.Zero ? null : hwnd.ToInt64(),
            processId,
            startTicks,
            snapshot?.Device,
            snapshot?.MonitorBounds.Left,
            snapshot?.MonitorBounds.Top,
            snapshot?.MonitorBounds.Right,
            snapshot?.MonitorBounds.Bottom,
            snapshot?.WorkArea.Left,
            snapshot?.WorkArea.Top,
            snapshot?.WorkArea.Right,
            snapshot?.WorkArea.Bottom);
    }

    private static MonitorWorkAreaSnapshot? ReadSnapshot(SessionState state)
    {
        if (string.IsNullOrWhiteSpace(state.MonitorDevice)
            || state.MonitorLeft is not int monitorLeft || state.MonitorTop is not int monitorTop
            || state.MonitorRight is not int monitorRight || state.MonitorBottom is not int monitorBottom
            || state.WorkLeft is not int workLeft || state.WorkTop is not int workTop
            || state.WorkRight is not int workRight || state.WorkBottom is not int workBottom)
            return null;

        return new MonitorWorkAreaSnapshot(
            state.MonitorDevice,
            new DesktopRect(monitorLeft, monitorTop, monitorRight, monitorBottom),
            new DesktopRect(workLeft, workTop, workRight, workBottom));
    }

    private static bool HasNewRecoveryMetadata(SessionState state) =>
        state.AppBarExpected.HasValue || state.AppBarHwnd.HasValue
        || state.ParentPid.HasValue || state.ParentStartTicks.HasValue
        || state.MonitorDevice != null || state.MonitorLeft.HasValue || state.MonitorTop.HasValue
        || state.MonitorRight.HasValue || state.MonitorBottom.HasValue
        || state.WorkLeft.HasValue || state.WorkTop.HasValue
        || state.WorkRight.HasValue || state.WorkBottom.HasValue;

    private static bool SameMonitor(MonitorWorkAreaSnapshot left, MonitorWorkAreaSnapshot right) =>
        string.Equals(left.Device, right.Device, StringComparison.OrdinalIgnoreCase)
        && left.MonitorBounds == right.MonitorBounds;

    private static bool TryGetCurrentProcessIdentity(out int processId, out long startTicks)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            processId = process.Id;
            startTicks = process.StartTime.ToUniversalTime().Ticks;
            return true;
        }
        catch
        {
            processId = 0;
            startTicks = 0;
            return false;
        }
    }

    private static bool IsProcessIdentityExited(int processId, long expectedStartTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return true;
            return process.StartTime.ToUniversalTime().Ticks != expectedStartTicks;
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }
}
