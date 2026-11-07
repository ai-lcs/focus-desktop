namespace focus_desktop.Services;

/// <summary>Bounded taskbar retry policy. The supplied callback performs one show-and-verify attempt.</summary>
public static class TaskbarRestorePolicy
{
    public static bool TryRestore(Func<int, bool> showAndVerify)
    {
        ArgumentNullException.ThrowIfNull(showAndVerify);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (showAndVerify(attempt)) return true;
        }

        return false;
    }
}
