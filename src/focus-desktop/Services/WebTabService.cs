using System.IO;
using Microsoft.Web.WebView2.Core;

namespace focus_desktop.Services;

/// <summary>
/// WebView2 Tab 管理：
/// - 单 Environment + 固定 UserDataFolder（exe 旁 focus-desktop-data/WebViewProfile）
///   → 所有 Tab 共享登录态，重启保持（spec §9）
/// - 每个 Tab 一个 WebView2 控件，激活时 Show 其余 Hide
/// - NavigationStarting 顶层白名单；NewWindowRequested 转内部 Tab；DownloadStarting 取消
/// - 懒加载：RegisterTab 只登记元数据，EnsureTabAsync 首次激活才建控件
/// - ProcessFailed 自愈：渲染进程崩溃 → 自动重建该 Tab；浏览器进程崩溃 → 全量重建
/// </summary>
public sealed class WebTabService : IDisposable
{
    private CoreWebView2Environment? _env;
    private readonly List<TabInfo> _tabs = new();
    private readonly string _profileDir;

    /// <summary>Tab 元数据（懒加载：View 可能为 null 直到 EnsureTabAsync）。</summary>
    public sealed record TabInfo(string Id, string Title, string InitialUrl,
        Microsoft.Web.WebView2.WinForms.WebView2? View);

    /// <summary>懒加载注册（不建控件）；返回可立即显示的 Tab。</summary>
    public TabInfo RegisterTab(string id, string title, string initialUrl)
    {
        var info = new TabInfo(id, title, initialUrl, null);
        _tabs.Add(info);
        return info;
    }

    public WebTabService()
    {
        _profileDir = Path.Combine(Paths.DataDir, "WebViewProfile");
        Directory.CreateDirectory(_profileDir);
    }

    public IReadOnlyList<TabInfo> Tabs => _tabs;

    /// <summary>WebView2 Runtime 缺失时抛带安装指引的异常（UI 层转友好卡片）。</summary>
    public async Task EnsureEnvironmentAsync()
    {
        if (_env != null) return;
        try
        {
            _env = await CoreWebView2Environment.CreateAsync(null, _profileDir, null);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new InvalidOperationException(
                "未检测到 WebView2 运行时（Edge 内核）。请安装：https://developer.microsoft.com/microsoft-edge/webview2/", ex);
        }
    }

    /// <summary>首次激活/后台预热时调用：真正创建 WebView2 控件。
    /// 预热与手点并发时用 _creating 去重，防止同一 Tab 创建两个控件。</summary>
    private readonly HashSet<string> _creating = new();

    public async Task<TabInfo> EnsureTabAsync(string id, System.Windows.Forms.Control host)
    {
        var idx = _tabs.FindIndex(t => t.Id == id);
        if (idx < 0) throw new KeyNotFoundException($"tab {id} 未注册");
        var info = _tabs[idx];
        if (info.View != null) return info; // 已建

        if (_creating.Contains(id))
        {
            // 另一个创建正在进行（预热中用户点了）：等它完成
            while (_creating.Contains(id)) await Task.Delay(100);
            return _tabs.First(t => t.Id == id);
        }
        _creating.Add(id);
        try
        {
            if (_env == null) throw new InvalidOperationException("先调 EnsureEnvironmentAsync");
            var view = new Microsoft.Web.WebView2.WinForms.WebView2
            {
                Dock = DockStyle.Fill,
                // 加载期间底色=主题深灰（默认白会在暗色站点上闪白屏——录屏确认的白屏就是它）
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x23, 0x26, 0x2C),
            };
            host.Controls.Add(view);
            await view.EnsureCoreWebView2Async(_env);

            var cfg = AppSettings.LoadOrDefault();
            WirePolicy(view, cfg);
            WireTitleSync(id, info.Title, view);
            WireProcessFailed(view);

            if (!string.IsNullOrEmpty(info.InitialUrl))
                view.CoreWebView2.Navigate(info.InitialUrl);
            info = info with { View = view };
            _tabs[idx] = info;
            return info;
        }
        finally
        {
            _creating.Remove(id);
        }
    }

    /// <summary>后台预热：错峰逐个创建 Tab 控件并导航（隐藏状态加载），
    /// 用户点击时页面已就绪 —— 消除"首次点击白屏/黑屏数秒"的卡顿。</summary>
    public async Task WarmupAllAsync(System.Windows.Forms.Control host)
    {
        foreach (var t in _tabs.ToList())
        {
            if (t.View != null) continue;
            try
            {
                await EnsureTabAsync(t.Id, host);
                await Task.Delay(2500); // 错峰：让上一个页面先完成关键渲染
            }
            catch { /* 单个失败不影响其余（懒加载兜底仍可用） */ }
        }
    }


    public bool CloseTab(string id)
    {
        var idx = _tabs.FindIndex(t => t.Id == id);
        if (idx < 0) return false;
        var info = _tabs[idx];
        _tabs.RemoveAt(idx);
        try { info.View?.Dispose(); } catch { }
        return true;
    }

    /// <summary>崩溃自愈：按原 URL 重建 Tab（保留在 Tab 条的位置与标题）。</summary>
    public async Task<bool> RecreateTabAsync(string id, System.Windows.Forms.Control host)
    {
        var idx = _tabs.FindIndex(t => t.Id == id);
        if (idx < 0) return false;
        var info = _tabs[idx];
        try { info.View?.Dispose(); } catch { }
        _tabs[idx] = info with { View = null };
        // 清掉 disposed 控件，重建
        await EnsureTabAsync(id, host);
        return true;
    }

    private void WireProcessFailed(Microsoft.Web.WebView2.WinForms.WebView2 view)
    {
        view.CoreWebView2.ProcessFailed += (_, e) =>
        {
            // 进程崩溃 → 通知 UI 层全量重建已激活过的 Tab（浏览器进程死时所有 Tab 一起完）
            ProcessFailed?.Invoke(e.ProcessFailedKind);
            Recovering?.Invoke();
        };
    }


    public static event Action<Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind>? ProcessFailed;
    public static event Action? Recovering;

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
                e.Handled = true;
                core.Navigate(uri.ToString());
            }
            else
            {
                e.Handled = true;
                Blocked?.Invoke(uri?.Host ?? "unknown");
            }
        };

        // 3) 下载默认取消（V1 简单策略；学习场景文件都在本地学习目录）
        core.DownloadStarting += (s, e) =>
        {
            e.Cancel = true;
        };
    }

    private static void WireTitleSync(string id, string title, Microsoft.Web.WebView2.WinForms.WebView2 view)
    {
        view.CoreWebView2.DocumentTitleChanged += (s, e) =>
        {
            var t = view.CoreWebView2.DocumentTitle;
            if (string.IsNullOrEmpty(t)) { TitleChanged?.Invoke(id, title); return; }
            // 页面标题限长 12 字符（B站首页标题很长会把 Tab 挤爆——视觉审查发现）
            var page = t.Length > 12 ? t[..12] + "…" : t;
            TitleChanged?.Invoke(id, $"{title} · {page}");
        };
    }

    public static event Action<string>? Blocked;
    public static event Action<string, string>? TitleChanged;

    public void Activate(string id)
    {
        // 只动可见性需要变化的控件：反复 Show/Hide 全部控件会触发 WinForms 布局风暴
        // （多 WebView2 控件时切 Tab 卡顿的直接原因）
        foreach (var t in _tabs)
        {
            if (t.View == null) continue;
            var want = t.Id == id;
            if (t.View.Visible != want) t.View.Visible = want;
        }
    }

    public void Dispose()
    {
        foreach (var t in _tabs) try { t.View?.Dispose(); } catch { }
    }
}
