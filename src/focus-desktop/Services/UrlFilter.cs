using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace focus_desktop.Services;

/// <summary>
/// 站点白名单判定（spec §8）：
/// - 顶层导航白名单（whitelist）：后缀匹配，允许整站内跳转
/// - 登录域（loginDomains）：OAuth/扫码跳转时放行
/// - 只拦顶层导航，不碰子资源（CDN/API 天然不受影响——它们不走 NavigationStarting）
/// - file:// 仅放行学习目录子树（内置 PDF/图片/TXT 查看器路径）
/// </summary>
public static class UrlFilter
{
    public static bool IsAllowed(Uri uri, AppSettings cfg)
    {
        // file://：只放行学习目录子树内的本地文件（PDF/图片/TXT 内置查看）
        if (uri.IsFile)
            return IsUnderStudyFolder(uri, cfg.StudyFolder);

        if (uri.Scheme != "http" && uri.Scheme != "https") return false;

        var host = uri.Host.ToLowerInvariant();
        var domains = cfg.Whitelist.Concat(cfg.LoginDomains)
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0)
            .ToHashSet();

        return domains.Any(d => host == d || host.EndsWith("." + d));
    }

    /// <summary>file:// 路径是否在学习目录子树内（防任意本地文件浏览）。</summary>
    private static bool IsUnderStudyFolder(Uri fileUri, string studyFolder)
    {
        return IsPathUnderDirectory(fileUri.LocalPath, studyFolder);
    }

    /// <summary>判断路径是否位于目录内，并优先按 Windows 实际目标路径校验 junction/symlink。</summary>
    public static bool IsPathUnderDirectory(string path, string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            var rootFull = CanonicalPath(root) ?? System.IO.Path.GetFullPath(root);
            var pathFull = CanonicalPath(path) ?? System.IO.Path.GetFullPath(path);
            rootFull = NormalizeForComparison(rootFull);
            pathFull = NormalizeForComparison(pathFull);
            return string.Equals(pathFull, rootFull, StringComparison.OrdinalIgnoreCase)
                || pathFull.StartsWith(rootFull + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? CanonicalPath(string path)
    {
        try
        {
            using var handle = CreateFile(path, 0, 0x00000007, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            for (var capacity = 260; capacity <= 32768; capacity *= 2)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)capacity, 0);
                if (length == 0) return null;
                if (length < capacity - 1) return buffer.ToString();
            }
        }
        catch { }
        return null;
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path[4..];
        return path;
    }

    private static string NormalizeForComparison(string path)
    {
        path = NormalizePath(path);
        var root = System.IO.Path.GetPathRoot(path);
        if (root != null && string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return root;
        return path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    /// <summary>供 UI 显示被拦截的 URL（顶栏提示条）。</summary>
    public static string? BlockedMessage(Uri uri) => $"已拦截非白名单页面：{uri.Host}";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
}
