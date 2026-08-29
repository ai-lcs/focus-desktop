using System.Text.Json;

namespace focus_desktop.Services;

/// <summary>
/// 站点白名单判定（spec §8）：
/// - 顶层导航白名单（whitelist）：后缀匹配，允许整站内跳转
/// - 登录域（loginDomains）：OAuth/扫码跳转时放行
/// - 只拦顶层导航，不碰子资源（CDN/API 天然不受影响——它们不走 NavigationStarting）
/// </summary>
public static class UrlFilter
{
    public static bool IsAllowed(Uri uri, AppSettings cfg)
    {
        // 特殊协议直接拦（file:// 只允许内嵌 PDF 路径单独处理，这里管顶层导航）
        if (uri.Scheme != "http" && uri.Scheme != "https") return false;

        var host = uri.Host.ToLowerInvariant();
        var domains = cfg.Whitelist.Concat(cfg.LoginDomains)
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0)
            .ToHashSet();

        return domains.Any(d => host == d || host.EndsWith("." + d));
    }

    /// <summary>供 UI 显示被拦截的 URL（顶栏提示条）。</summary>
    public static string? BlockedMessage(Uri uri) => $"已拦截非白名单页面：{uri.Host}";
}
