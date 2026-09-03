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
                // 控件初始化前的 WinForms 底色也压成深灰：CoreWebView2 就绪前控件画的是
                // BackColor（默认 SystemColors.Control=#F0F0F0）——2026-08-31 视频里
                // PDF 首开 0.75s 纯白闪即此来源（创建期控件尚未初始化时的裸底色）
                BackColor = System.Drawing.Color.FromArgb(0x23, 0x26, 0x2C),
                // v0.5.2：预热/懒加载全程以「隐藏」形态创建（先于 host.Controls.Add 生效）。
                // 修复 2026-08-31 19:28 视频回归：预热把 WebHost 设 Visible + 控件默认可见
                // → B站白底加载页盖在首页上空 + airspace 闪烁 1 秒（用户看到"启动 1 秒大卡顿"）。
                // 隐藏创建同时绕开旧坏死陷阱——其触发条件是「折叠宿主内创建 visible=true 子控件」，
                // 隐藏子控件不在其列（Tauri/wry 同款形态）。首开激活由 Activate 显式 Resume+显示。
                Visible = false,
            };
            host.Controls.Add(view);
            await view.EnsureCoreWebView2Async(_env);

            var cfg = AppSettings.LoadOrDefault();
            // 站点 preset 的域名真相在 SiteCatalog。这里不能只信 config 中旧的
            // whitelist，否则升级后新增/迁移的域名（如 notebook.google.com）
            // 会在真正的 WebView 导航策略层仍被拦截。
            SiteCatalog.ComputeEffectiveDomains(cfg, out var effectiveWhitelist, out var effectiveLoginDomains);
            cfg.Whitelist = effectiveWhitelist;
            cfg.LoginDomains = effectiveLoginDomains;
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

    /// <summary>预热：全程隐藏创建（控件 Visible=false 先于入宿主、宿主保持 Collapsed）。
    /// v0.5.1 曾把 WebHost 临时设 Visible 以规避「折叠宿主内创建坏死」——但 WebHost 在
    /// 主 Grid 中位于 HomeView 之后 = WPF z 序最顶，展开瞬间 B站白底加载页盖住首页并产生
    /// airspace 闪烁约 1 秒（2026-08-31 19:28 视频回归，"启动一两秒后大卡顿"）。
    /// v0.5.2 定论：坏死陷阱的触发条件是「折叠宿主内创建 visible=true 子控件」，与宿主
    /// 折叠无关的是控件自身的可见性——控件以隐藏形态创建即安全（Tauri/wry 同款），
    /// 因此宿主可保持 Collapsed，预热完全不可见。首开激活由 Activate 显式 Resume+显示。</summary>
    public async Task WarmupAllAsync(System.Windows.Forms.Control host)
    {
        foreach (var t in _tabs.ToList())
        {
            if (t.View != null) continue;
            try
            {
                // v0.5.2 关键修复：EnsureTabAsync 返回更新后的 TabInfo（_tabs 里的记录已换新），
                // foreach 变量 t 仍是旧的（View=null）——v0.5.1 用 t.View!.Visible 抛 NRE 被吞，
                // 导致「创建后隐藏+错峰」从未执行：4 个站以可见状态盖在首页上空狂闪 ~1.3 秒
                // （2026-08-31 19:28 视频的启动大卡顿）。
                // v0.5.2 同时移除 ExecuteScript 探活：其防御对象（折叠宿主内可见创建的坏死视图）
                // 已被「隐藏创建」从根上消灭；实测它对 chatgpt/gemini 等重站 1.5s 未提交导航的
                // 场景误报 blank → 销毁已就绪页面回退懒加载（首开又变慢），还写吓人的 crash 日志。
                var created = await EnsureTabAsync(t.Id, host);
                // 幂等保险（EnsureTabAsync 已以 Visible=false 隐藏创建）
                created.View!.Visible = false;
                await Task.Delay(1500); // 错峰 + 让页面完成关键渲染
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
        // v0.4.3（用户 2026-08-31 指示）：Tab 只显示站点短名（学习文件/哔哩哔哩/Chat GPT/
        // DeepSeek/AI Studio），不拼页面标题——拼标题会把 Tab 横向拖长（B站标题特长）。
        // 页面标题变化不再反映到 Tab 文字；仍订阅事件，保证短名在标题抖动下稳定。
        view.CoreWebView2.DocumentTitleChanged += (s, e) =>
        {
            TitleChanged?.Invoke(id, title);
        };
    }

    public static event Action<string>? Blocked;
    public static event Action<string, string>? TitleChanged;

    /// <summary>
    /// Tab 切换（v0.4.2 定稿：网页间「全员常显 + WinForms z 序」，像素实测验证）。
    ///
    /// 根因（2026-08-31 像素级取证，50ms 粒度逐帧）：
    /// Visible=false 隐藏 WebView2 → Chromium 挂起并丢弃合成表面；
    /// Visible=true 恢复时重建表面，期间露出窗口底色——切换后 50~250ms 纯白闪(#F0F0F0)，
    /// 且被挂起的站点 Resume 后常不能及时重绘（chatgpt→bili 800ms 仍黑屏）。
    /// 旧版靠 WPF 层 SwitchMask 遮罩盖白闪——但 WebView2 是 airspace HWND 凌驾于整个
    /// WPF 层之上，遮罩永远压在网页下面，物理上盖不住（白闪帧里无任何遮罩色）。
    ///
    /// 本方案：网页互切时所有 WebView 恒 Visible=true（合成表面常驻、渲染进程不挂起，
    /// 等同浏览器后台标签），切换仅 Control.BringToFront() 改 WinForms 同层 z 序——
    /// 不动窗口显示状态 = 无重建 = 瞬时。实测：切换 50ms 内画面即为目标页、零白闪；
    /// 被遮挡的页面保留画面，B站视频切走不中断。
    /// 注意：BringToFront 是 WinForms 层调用（子 HWND 在宿主面板兄弟链内排序），
    /// 与曾用 Win32 SetWindowPos(HWND_TOP) 提顶不同——后者会越出宿主层级盖住 WPF 顶栏
    /// （2026-08-31 上午 z 序方案三连败的根源），勿混淆回退。
    /// 目标为首页/文件（非网页）时才隐藏全部 WebView（此时可接受重建代价）。
    ///
    /// 完整实验档案（七个被否决方案 + 取证方法）见 windows-desktop-app skill：
    /// references/webview2-tab-switching.md
    /// </summary>
    /// <summary>网页互切后触发（参数 false=已就绪页秒切），创建期切换触发 true
    /// （首开站/PDF 的等待反馈）。v0.5.1：WPF 遮罩已删（airspace 之下无效），保留事件签名，
    /// 让 UI 层将来可挂进度指示等真正画得上去的反馈层。</summary>
    public static event Action<bool>? Switched;

    /// <summary>
    /// Tab 切换（v0.5.1：网页间「全员常显 + WinForms z 序」；非网页目标不再冻结 WebView）。
    ///
    /// 两处关键修正（2026-08-31 视频逐帧取证定罪）：
    /// ① 目标为首页/文件时旧版把所有 WebView Visible=false —— 重新触发 Chromium 挂起 +
    ///    丢合成表面，经文件页中转回网页时必白闪（视频中 6.5s 切回 B 站白闪实锤）。
    ///    现改为一律不动可见性：WebView 恒 visible，切到首页/文件时由不透明 WPF 视图盖住
    ///    （等同浏览器后台标签，官方支持：visible 被遮挡的 WebView 继续渲染不挂起）。
    /// ② Switched 事件带「是否创建期」参数：仅创建期切换（首开站/PDF，用户需等待反馈）
    ///    才闪遮罩；z 序秒切不闪（此前每次切换都闪，首开时黑白翻转纯粹添乱）。
    ///
    /// 原方案（网页互切）不变：所有 WebView 恒 Visible=true（合成表面常驻、渲染进程不挂起），
    /// 切换仅 Control.BringToFront() 改 WinForms 同层 z 序——不动窗口显示状态 = 无重建 = 瞬时。
    /// </summary>
    public void Activate(string? id)
    {
        var target = id == null ? null : _tabs.FirstOrDefault(t => t.Id == id);
        if (target?.View != null)
        {
            foreach (var t in _tabs)
            {
                if (t.View == null) continue;
                if (!t.View.Visible)
                {
                    try { t.View.CoreWebView2.Resume(); } catch { }
                    t.View.Visible = true;
                }
            }
            target.View.BringToFront();
        }
        // else：目标为首页/文件（非网页）——一律不动 WebView 可见性（见上①）
        Switched?.Invoke(target != null && target.View == null); // 仅创建期=true
    }

    public void Dispose()
    {
        foreach (var t in _tabs) try { t.View?.Dispose(); } catch { }
    }
}
