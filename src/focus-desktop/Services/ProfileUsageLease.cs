using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace focus_desktop.Services;

/// <summary>Coordinates one profile across sessions; its guard remains outside the deletable profile.</summary>
internal sealed class ProfileUsageLease : IDisposable
{
    private readonly FileStream _guard;

    private ProfileUsageLease(FileStream guard) => _guard = guard;

    internal static bool TryAcquire(string dataDirectory, out ProfileUsageLease? lease, out string reason)
    {
        lease = null;
        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
                throw new IOException("配置目录没有独立的父目录。");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            var guardPath = Path.Combine(parent, $".focus-desktop-profile-{key}.lock");
            var stream = new FileStream(guardPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            lease = new ProfileUsageLease(stream);
            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reason = "配置目录正在使用或无法取得独占访问权；本次未读取、恢复或删除该目录。";
            return false;
        }
    }

    public void Dispose() => _guard.Dispose();
}
