namespace focus_desktop.Services;

/// <summary>
/// 站点目录与解析（Public v1 数据驱动核心）：
/// - Preset 站点：真相在代码（BuiltInSites），config 只存 id（升级 app 可修域名）；
/// - Custom 站点：用户首次向导添加，config 自含全部字段；
/// - Resolve()：config → 运行时三件套（Tab 列表、Whitelist、LoginDomains）的纯函数，可单测；
/// - Legacy：v0.5.x 老配置（无 schemaVersion 但有 whitelist）→ 4 个 preset + 原样保留旧域名列表；
/// - ParseCustomInput：自定义站点的输入归一化（host 提取、www 去除、域推导、查重），
///   校验失败返回 null——调用方（向导）只提示「网址无效或与已有站点重复」，不暴露工程概念。
/// </summary>
public static class SiteCatalog
{
    /// <summary>站点定义（preset 与 custom 统一形状；custom 的 LoginDomains 恒为空）。
    /// TabKey = tab 条/自动化用的业务 id（preset 即字典键，含历史别名 "gemini"；custom 为分配 id）；
    /// Id = 站点身份（tab 多开等既有逻辑按它分组，别名键共享同一身份）。</summary>
    public sealed record SiteDef(
        string Id, string TabKey, string Title, string Url,
        IReadOnlyList<string> WhitelistDomains,
        IReadOnlyList<string> LoginDomains,
        bool IsPreset, bool AllowMulti);

    /// <summary>config.json 的 sites[] 元素（System.Text.Json 直接序列化）。</summary>
    public sealed class SiteEntry
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }        // custom 必填；preset 忽略（以代码为准）
        public string? Url { get; set; }          // custom 必填；preset 忽略
        public List<string>? Domains { get; set; } // custom 必填；preset 忽略
    }

    /// <summary>Preset 目录。Id 与 Tab AutomationId（tab_&lt;id&gt;）直接对应，禁止改名。</summary>
    public static readonly IReadOnlyDictionary<string, SiteDef> BuiltInSites =
        new Dictionary<string, SiteDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["bili"] = new("bili", "bili", "哔哩哔哩", "https://www.bilibili.com",
                new[] { "bilibili.com" }, new[] { "passport.bilibili.com" }, true, true),
            ["chatgpt"] = new("chatgpt", "chatgpt", "Chat GPT", "https://chatgpt.com",
                new[] { "chatgpt.com" }, new[] { "auth.openai.com", "cdn.auth0.com" }, true, true),
            ["aistudio"] = new("aistudio", "aistudio", "AI Studio", "https://aistudio.google.com",
                new[] { "aistudio.google.com", "gemini.google.com" },
                new[] { "accounts.google.com" }, true, true),
            // 兼容别名：v0.5.4 的硬编码 id 与 verify-tabs.ps1 的断言都叫 "gemini"——同一站两个登记名。
            ["gemini"] = new("aistudio", "gemini", "AI Studio", "https://aistudio.google.com",
                new[] { "aistudio.google.com", "gemini.google.com" },
                new[] { "accounts.google.com" }, true, true),
            ["deepseek"] = new("deepseek", "deepseek", "DeepSeek", "https://chat.deepseek.com",
                new[] { "deepseek.com" }, Array.Empty<string>(), true, true),
            // v1.0.3：NotebookLM 升为第 5 preset（用户 2026-09-03 指示，社区反馈）。tab/首页短名 NotebookLM。
            ["notebooklm"] = new("notebooklm", "notebooklm", "NotebookLM", "https://notebooklm.google.com",
                new[] { "notebooklm.google.com" }, new[] { "accounts.google.com" }, true, true),
        };

    /// <summary>默认站点集（Setup 向导初始勾选 / legacy 配置的运行时展开）。
    /// 注意用历史 id "gemini"（v0.5.4 硬编码 id 与 verify-tabs.ps1 断言所系），BuiltInSites 经别名解析。
    /// v1.0.3：加入第 5 站 notebooklm。</summary>
    public static readonly IReadOnlyList<string> DefaultPresetIds =
        new[] { "bili", "chatgpt", "gemini", "deepseek", "notebooklm" };

    /// <summary>域名/白名单条目的归一化：小写 + 去 www. 前缀 + Trim。所有比较前必须走这里。</summary>
    public static string NormalizeDomain(string raw)
    {
        var d = (raw ?? "").Trim().ToLowerInvariant();
        while (d.StartsWith("www.", StringComparison.Ordinal)) d = d[4..];
        return d;
    }

    /// <summary>两个归一化域名是否「同一站点域」（相等或互为子域后缀）。</summary>
    public static bool SameOrSubdomain(string a, string b) =>
        a == b || a.EndsWith("." + b, StringComparison.Ordinal) || b.EndsWith("." + a, StringComparison.Ordinal);

    /// <summary>
    /// 解析 config → 运行时站点集（纯函数）：
    /// sites 为 null/空 = legacy 或未配置 → 全部 preset；
    /// 逐条展开 preset id（未知 id 跳过）与 custom 自含条目（畸形跳过）；
    /// 重复 TabKey 后者让位（"gemini" 与 "aistudio" 是同一站的两个 TabKey，可同时存在）；结果按 config 出现顺序。
    /// </summary>
    public static List<SiteDef> ResolveSites(AppSettings cfg)
    {
        var result = new List<SiteDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = cfg.Sites;
        if (entries == null || entries.Count == 0)
        {
            foreach (var id in DefaultPresetIds) { result.Add(BuiltInSites[id]); seen.Add(id); }
            return result;
        }
        foreach (var e in entries)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Id) || !seen.Add(e.Id)) continue;
            if (BuiltInSites.TryGetValue(e.Id, out var preset))
            {
                result.Add(preset);
                continue;
            }
            // custom：必须自含 title/url/domains，且 URL 合法
            if (string.IsNullOrWhiteSpace(e.Title) || string.IsNullOrWhiteSpace(e.Url)) continue;
            if (!TryParseHttpUrl(e.Url, out _, out _)) continue;
            var domains = (e.Domains ?? new List<string>())
                .Select(NormalizeDomain).Where(d => d.Length > 0).Distinct().ToArray();
            if (domains.Length == 0) continue;
            result.Add(new SiteDef(e.Id, e.Id, e.Title.Trim(), e.Url.Trim(), domains,
                Array.Empty<string>(), false, false));
        }
        return result;
    }

    /// <summary>
    /// 计算运行时白名单/登录域（纯函数，T2 起替换启动时的手工合并）：
    /// - 站点域名并集 → Whitelist；登录域并集 → LoginDomains（均归一化去重）；
    /// - 两边重复（用户 custom 域撞上 preset 登录域）→ 从 LoginDomains 移除，Whitelist 优先；
    /// - legacy 配置（sites 为空）额外并上 cfg.Whitelist / cfg.LoginDomains 原值（老配置真相在列表里）。
    /// </summary>
    public static void ComputeEffectiveDomains(AppSettings cfg, out List<string> whitelist, out List<string> loginDomains)
    {
        var wl = new List<string>();
        var ld = new List<string>();
        void Add(List<string> list, string d)
        {
            d = NormalizeDomain(d);
            if (d.Length > 0 && !list.Contains(d)) list.Add(d);
        }
        var legacy = cfg.Sites == null || cfg.Sites.Count == 0;
        foreach (var s in ResolveSites(cfg))
        {
            foreach (var d in s.WhitelistDomains) Add(wl, d);
            foreach (var d in s.LoginDomains) Add(ld, d);
        }
        if (legacy)
        {
            foreach (var d in cfg.Whitelist) Add(wl, d);
            foreach (var d in cfg.LoginDomains) Add(ld, d);
        }
        // 去重只在「完全相等」级别：passport.bilibili.com 是 bilibili.com 的子域，
        // 但它作为登录域必须保留（EndsWith 级别去重会把它误删——T3 用例定罪）。
        // 语义：仅当用户 custom 域与 preset 登录域完全相同（==）时，whitelist 优先，从 login 移除。
        ld.RemoveAll(d => wl.Contains(d));
        whitelist = wl;
        loginDomains = ld;
    }

    /// <summary>为用户自定义站点生成不与现有冲突的 id（site / site-2 / …）。</summary>
    public static string AllocateCustomId(IEnumerable<string> existingIds)
    {
        var taken = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        var id = "site";
        var n = 1;
        while (taken.Contains(id)) id = $"site-{++n}";
        return id;
    }

    /// <summary>
    /// 自定义站点输入归一化（向导「添加」按钮的唯一入口）。
    /// 成功：返回 SiteEntry（id 已分配、title 缺省取 host、domains=[host]）；
    /// 失败：返回 null（调用方给统一提示，不区分原因细节——「网址无效或与已有站点重复」）。
    /// 拒绝：非 http(s)、无 host、IP 字面量、与已有域名/登录域冲突、与已有站点 URL 相同。
    /// extraConflictDomains：向导场景传入「已勾选但尚未写入 Sites 的 preset 域」——勾选状态
    /// 是独立 UI 状态，不在 cfg.Sites 里，不传则 preset 域漏检（T6 实测定罪：勾选 B 站后仍可添加
    /// sub.bilibili.com 为 custom 站，白名单重复且 custom 抢到 site-2 id）。
    /// </summary>
    public static SiteEntry? ParseCustomInput(string? rawUrl, string? rawTitle, AppSettings cfg,
        IEnumerable<string>? extraConflictDomains = null)
    {
        if (!TryParseHttpUrl(rawUrl, out var uri, out var host) || host == null) return null;

        ComputeEffectiveDomains(cfg, out var wl, out var ld);
        var allDomains = wl.Concat(ld);
        if (extraConflictDomains != null) allDomains = allDomains.Concat(extraConflictDomains);
        if (allDomains.Any(d => SameOrSubdomain(host, d))) return null;

        var entries = cfg.Sites ??= new List<SiteEntry>();
        var url = uri!.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (entries.Any(e => e.Url != null &&
            string.Equals(e.Url.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)))
            return null;

        var title = string.IsNullOrWhiteSpace(rawTitle) ? host : rawTitle.Trim();
        return new SiteEntry
        {
            Id = AllocateCustomId(entries.Select(e => e.Id)),
            Title = title,
            Url = url,
            Domains = new List<string> { host },
        };
    }

    /// <summary>http(s) URL 校验 + host 提取（补全 https://；拒绝 IP 字面量）。</summary>
    private static bool TryParseHttpUrl(string? raw, out Uri? uri, out string? host)
    {
        uri = null; host = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != "http" && u.Scheme != "https") return false;
        var h = NormalizeDomain(u.Host);
        if (h.Length == 0 || h.Any(char.IsWhiteSpace)) return false;
        if (System.Net.IPAddress.TryParse(h, out _)) return false; // 学习场景不放行裸 IP
        uri = u; host = h;
        return true;
    }
}
