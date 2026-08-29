namespace focus_desktop.Services;

/// <summary>
/// 会话脏标志（spec §4）：EnterFocusMode 第一步写 true，全部恢复完成才写 false。
/// 下次启动若读到 true = 上次非正常退出 → 先恢复系统状态。
/// </summary>
public static class RecoveryService
{
    private sealed record SessionState(bool FocusModeActive);

    public static void MarkActive()
    {
        Paths.EnsureDirectories();
        AtomicFile.WriteAllText(Paths.SessionStateFile,
            $$"""{"focus_mode_active": true}""");
    }

    public static void MarkClean()
    {
        Paths.EnsureDirectories();
        AtomicFile.WriteAllText(Paths.SessionStateFile,
            $$"""{"focus_mode_active": false}""");
    }

    public static bool WasActiveLastTime()
    {
        try
        {
            if (!File.Exists(Paths.SessionStateFile)) return false;
            var text = File.ReadAllText(Paths.SessionStateFile);
            return text.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // 读不了就当没有——恢复动作本身是幂等的，多恢复一次无害
        }
    }
}
