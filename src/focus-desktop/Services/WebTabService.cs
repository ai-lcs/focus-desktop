using System.IO;
using Microsoft.Web.WebView2.Core;

namespace focus_desktop.Services;

/// <summary>
/// WebView2 Tab 管理：
/// - 单 Environment + 固定 UserDataFolder（exe 旁 focus-desktop-data/WebViewProfile）
///   → 所有 Tab 共享登录态，重启保持（spec §9）
/// - 每个 Tab 一个 WebView2 控件，激活时 Show 其余 Hide
/// - NavigationStarting 顶层白名单；NewWindowRequested 转内部 Tab；DownloadStarting 取消
/// </summary>
public sealed class WebTabService : IDisposable
{
    private CoreWebView2Environment? _env;
    private readonly List<Tab> _tabs = new();
    private readonly string _profileDir;

    public sealed record Tab(string Id, string Title, Microsoft.Web.WebView2.WinForms.WebView2 View);

    public WebTabService()
    {
        _profileDir = Path.Combine(Paths.DataDir, "WebViewProfile");
        Directory.CreateDirectory(_profileDir);
    }

    public IReadOnlyList<Tab> Tabs => _tabs;

    /// <summary>应用启动时调用一次（WPF 里 WebView2 控件创建在 UI 线程）。</summary>
    public async Task EnsureEnvironmentAsync()
    {
        if (_env != null) return;
        _env = await CoreWebView2Environment.CreateAsync(null, _profileDir, null);
    }

    public async Task<Tab> CreateTabAsync(string id, string title, string initialUrl, System.Windows.Forms.Control host)
    {
        if (_env == null) throw new InvalidOperationException("先调 EnsureEnvironmentAsync");
        var view = new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = DockStyle.Fill,
        };
        host.Controls.Add(view);
        await view.EnsureCoreWebView2Async(_env);

        var cfg = AppSettings.LoadOrDefault();
        WirePolicy(view, cfg);
        WireTitleSync(id, title, view);

        if (!string.IsNullOrEmpty(initialUrl))
            view.CoreWebView2.Navigate(initialUrl);
        _tabs.Add(new Tab(id, title, view));
        return _tabs[^1];
    }

    private static void WirePolicy(Microsoft.Web.WebView2.WinForms.WebView2 view, AppSettings cfg)
    {
        var core = view.CoreWebView2;

        // 1) 顶层导航白名单
        core.NavigationStarting += (s, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && !UrlFilter.IsAllowed(uri, cfg))
            {
                e.Cancel = true;
                Blocked?.Invoke(uri.Host);
            }
        };

        // 2) 新窗口一律转内部导航（同 Tab 内打开，不开系统浏览器）
        core.NewWindowRequested += (s, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && UrlFilter.IsAllowed(uri, cfg))
            {
                // 允许的站点：在当前 Tab 打开（简单化：target=_blank 同样导航当前 Tab）
                e.Handled = true;
                core.Navigate(uri.ToString());
            }
            else
            {
                e.Handled = true; // 非白名单新窗口：直接吞掉
                Blocked?.Invoke(uri?.Host ?? "unknown");
            }
        };

        // 3) 下载默认取消（V1 简单策略；Step 4 PDF 都在本地）
        core.DownloadStarting += (s, e) =>
        {
            e.Cancel = true;
        };

        // 4) source.com/xxx.pdf 的浏览内置 PDF 查看
    }

    private static void WireTitleSync(string id, string title, Microsoft.Web.WebView2.WinForms.WebView2 view)
    {
        // 顶栏显示 Tab 标题 + 页面标题（B 站网课题目）
        view.CoreWebView2.DocumentTitleChanged += (s, e) =>
        {
            var t = view.CoreWebView2.DocumentTitle;
            TitleChanged?.Invoke(id, string.IsNullOrEmpty(t) ? title : $"{title} · {t}");
        };
    }

    public static event Action<string>? Blocked;
    public static event Action<string, string>? TitleChanged;

    public void Activate(string id)
    {
        foreach (var t in _tabs)
            t.View.Visible = t.Id == id;
    }

    public void Dispose()
    {
        foreach (var t in _tabs) t.View.Dispose();
    }
}
