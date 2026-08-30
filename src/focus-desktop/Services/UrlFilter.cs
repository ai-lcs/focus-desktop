using System.Text.Json;

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
        try
        {
            if (string.IsNullOrWhiteSpace(studyFolder)) return false;
            var path = fileUri.LocalPath; // 已解码的本地路径
            var root = System.IO.Path.GetFullPath(studyFolder)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var full = System.IO.Path.GetFullPath(path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>供 UI 显示被拦截的 URL（顶栏提示条）。</summary>
    public static string? BlockedMessage(Uri uri) => $"已拦截非白名单页面：{uri.Host}";
}
