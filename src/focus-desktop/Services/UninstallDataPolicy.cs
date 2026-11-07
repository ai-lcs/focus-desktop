using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace focus_desktop.Services;

internal static class UninstallDataPolicy
{
    private const string LocalDataDirectoryName = Paths.InstalledDataDirectoryName;
    private static readonly IntPtr CurrentServer = IntPtr.Zero;

    public static bool CanRemoveUserData(string dataDir, out string reason)
    {
        reason = "无法验证用户数据归属，已保留数据。";

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData) || string.IsNullOrWhiteSpace(dataDir))
            {
                reason = "无法解析当前用户的数据目录，已保留数据。";
                return false;
            }
            dataDir = NormalizeDirectoryPath(dataDir);

            if (!TryGetInteractiveSessionUserSid(out var sessionSid))
            {
                reason = "无法确认当前交互桌面的账户身份，已保留数据。";
                return false;
            }

            using var currentIdentity = WindowsIdentity.GetCurrent();
            var currentSid = currentIdentity.User;
            if (currentSid is null || !currentSid.Equals(sessionSid))
            {
                reason = "当前提权账户与交互桌面账户不同，已保留数据。";
                return false;
            }

            if (!TryGetPathAttributes(dataDir, out var directoryExists, out var directoryAttributes))
            {
                reason = "无法读取数据目录属性，已保留数据。";
                return false;
            }
            if (directoryExists && (directoryAttributes & FileAttributes.Directory) == 0)
            {
                reason = "数据路径不是目录，已保留数据。";
                return false;
            }

            if (!TryGetPathAttributes(Path.Combine(dataDir, "portable.flag"), out var dataPortableFlag, out _)
                || !TryGetPathAttributes(Path.Combine(AppContext.BaseDirectory, "portable.flag"), out var appPortableFlag, out _))
            {
                reason = "无法确认是否为便携版数据，已保留数据。";
                return false;
            }
            var isReparsePoint = directoryExists
                && (directoryAttributes & FileAttributes.ReparsePoint) != 0;
            var hasPortableFlag = dataPortableFlag || appPortableFlag;
            SecurityIdentifier? ownerSid = null;
            if (directoryExists)
            {
                var directorySecurity = new DirectoryInfo(dataDir).GetAccessControl();
                ownerSid = directorySecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            }

            return IsIdentityAuthorized(
                dataDir,
                localAppData,
                currentSid,
                sessionSid,
                ownerSid,
                directoryExists,
                isReparsePoint,
                hasPortableFlag,
                out reason);
        }
        catch
        {
            reason = "无法验证用户数据归属，已保留数据。";
            return false;
        }
    }

    internal static bool IsIdentityAuthorized(
        string dataDir,
        string localAppData,
        SecurityIdentifier? processSid,
        SecurityIdentifier? sessionSid,
        SecurityIdentifier? ownerSid,
        bool directoryExists,
        bool isReparsePoint,
        bool hasPortableFlag,
        out string reason)
    {
        reason = "无法验证用户数据归属，已保留数据。";

        try
        {
            if (string.IsNullOrWhiteSpace(dataDir) || string.IsNullOrWhiteSpace(localAppData))
            {
                reason = "无法解析当前用户的数据目录，已保留数据。";
                return false;
            }

            var expectedPath = NormalizeDirectoryPath(Path.Combine(localAppData, LocalDataDirectoryName));
            var requestedPath = NormalizeDirectoryPath(dataDir);
            if (!string.Equals(expectedPath, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                reason = "目标不是当前用户的标准 Focus Desk 数据目录，已保留数据。";
                return false;
            }

            if (hasPortableFlag)
            {
                reason = "便携版数据不会由安装程序清理。";
                return false;
            }

            if (processSid is null || sessionSid is null || !processSid.Equals(sessionSid))
            {
                reason = "当前提权账户与交互桌面账户不同，已保留数据。";
                return false;
            }

            if (!directoryExists)
            {
                reason = string.Empty;
                return true;
            }

            if (isReparsePoint)
            {
                reason = "数据目录是重解析点，已保留数据。";
                return false;
            }

            if (ownerSid is null || !ownerSid.Equals(sessionSid))
            {
                reason = "数据目录所有者与交互桌面账户不一致，已保留数据。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch
        {
            reason = "无法验证用户数据归属，已保留数据。";
            return false;
        }
    }

    private static bool TryGetInteractiveSessionUserSid(out SecurityIdentifier sessionSid)
    {
        sessionSid = null!;
        var sessionId = Process.GetCurrentProcess().SessionId;

        if (!TryQuerySessionValue(sessionId, WtsInfoClass.WtsConnectState, out var stateBuffer))
            return false;

        try
        {
            if (Marshal.ReadInt32(stateBuffer) != (int)WtsConnectState.Active)
                return false;
        }
        finally
        {
            WTSFreeMemory(stateBuffer);
        }

        if (!TryQuerySessionString(sessionId, WtsInfoClass.WtsUserName, out var userName)
            || string.IsNullOrWhiteSpace(userName))
            return false;

        if (!TryQuerySessionString(sessionId, WtsInfoClass.WtsDomainName, out var domainName))
            return false;

        var accountName = string.IsNullOrWhiteSpace(domainName)
            ? userName
            : $"{domainName}\\{userName}";

        try
        {
            sessionSid = new NTAccount(accountName).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier
                ?? null!;
            return sessionSid is not null;
        }
        catch
        {
            sessionSid = null!;
            return false;
        }
    }

    private static bool TryQuerySessionString(int sessionId, WtsInfoClass infoClass, out string value)
    {
        value = string.Empty;
        if (!TryQuerySessionValue(sessionId, infoClass, out var buffer))
            return false;

        try
        {
            value = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return true;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static bool TryQuerySessionValue(int sessionId, WtsInfoClass infoClass, out IntPtr buffer)
    {
        return WTSQuerySessionInformation(CurrentServer, sessionId, infoClass, out buffer, out _)
            && buffer != IntPtr.Zero;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryGetPathAttributes(string path, out bool exists, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            exists = false;
            attributes = default;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            exists = false;
            attributes = default;
            return true;
        }
        catch
        {
            exists = false;
            attributes = default;
            return false;
        }
    }

    [DllImport("Wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private enum WtsInfoClass
    {
        WtsUserName = 5,
        WtsConnectState = 8,
        WtsDomainName = 7
    }

    private enum WtsConnectState
    {
        Active = 0
    }
}
