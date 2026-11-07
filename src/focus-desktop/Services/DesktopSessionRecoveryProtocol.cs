namespace focus_desktop.Services;

public sealed record RecoverySessionSnapshot(
    bool IsActive,
    string? Generation,
    int? ParentProcessId,
    long? ParentStartTicks);

/// <summary>Pure recovery ordering rules, kept separate from the Win32 and file boundaries.</summary>
public static class DesktopSessionRecoveryProtocol
{
    public static bool Matches(RecoverySessionSnapshot? snapshot, string generation,
        int parentProcessId, long parentStartTicks) =>
        snapshot is { IsActive: true }
        && !string.IsNullOrWhiteSpace(generation)
        && string.Equals(snapshot.Generation, generation, StringComparison.Ordinal)
        && snapshot.ParentProcessId == parentProcessId
        && snapshot.ParentStartTicks == parentStartTicks;

    public static bool TryRunWatchdogRecovery(
        Func<RecoverySessionSnapshot?> readCurrentSession,
        string generation,
        int parentProcessId,
        long parentStartTicks,
        Func<int, long, bool> parentIdentityHasExited,
        Func<bool> restoreTaskbar,
        Func<RecoverySessionSnapshot, bool> confirmAppBarReleased,
        Func<RecoverySessionSnapshot, bool> markCleanIfStillOwned)
    {
        ArgumentNullException.ThrowIfNull(readCurrentSession);
        ArgumentNullException.ThrowIfNull(parentIdentityHasExited);
        ArgumentNullException.ThrowIfNull(restoreTaskbar);
        ArgumentNullException.ThrowIfNull(confirmAppBarReleased);
        ArgumentNullException.ThrowIfNull(markCleanIfStillOwned);

        if (!CanProceed(readCurrentSession(), generation, parentProcessId, parentStartTicks,
                parentIdentityHasExited))
            return false;

        if (!CanProceed(readCurrentSession(), generation, parentProcessId, parentStartTicks,
                parentIdentityHasExited))
            return false;
        if (!restoreTaskbar()) return false;

        var afterShow = readCurrentSession();
        if (!CanProceed(afterShow, generation, parentProcessId, parentStartTicks,
                parentIdentityHasExited)
            || !confirmAppBarReleased(afterShow!))
            return false;

        var beforeMarkClean = readCurrentSession();
        if (!CanProceed(beforeMarkClean, generation, parentProcessId, parentStartTicks,
                parentIdentityHasExited))
            return false;

        return markCleanIfStillOwned(beforeMarkClean!);
    }

    private static bool CanProceed(RecoverySessionSnapshot? session, string generation,
        int parentProcessId, long parentStartTicks,
        Func<int, long, bool> parentIdentityHasExited) =>
        Matches(session, generation, parentProcessId, parentStartTicks)
        && parentIdentityHasExited(parentProcessId, parentStartTicks);
}
