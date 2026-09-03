using System.IO;
using System.Text.Json;
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
            using var stream = new FileStream(Paths.SessionStateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("focus_mode_active", out var active)
                && active.ValueKind == JsonValueKind.True;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            // 状态损坏/不可读时保守恢复；Show/MarkClean 都是幂等操作。
            return true;
        }
    }
}
