using focus_desktop.Services;

namespace focus_desktop.Tests;

/// <summary>
/// 白名单判定逻辑的自测（--urltest 参数触发，控制台输出后退出）。
/// 放在主工程里是因为 UrlFilter 依赖 AppSettings（无独立测试工程，V1 从简）。
/// </summary>
public static class UrlSelfTest
{
    public static int Run()
    {
        var cfg = new AppSettings();
        cfg.StudyFolder = @"D:\杂文件\focus"; // file:// 学习目录子树用例的基准
        int pass = 0, fail = 0;

        void Check(string url, bool expected)
        {
            var ok = UrlFilter.IsAllowed(new Uri(url), cfg);
            if (ok == expected) { pass++; Console.WriteLine($"  PASS {url} -> {ok}"); }
            else { fail++; Console.WriteLine($"  FAIL {url} -> {ok} (期望 {expected})"); }
        }

        // ---- T3 新增：SiteCatalog 自测辅助函数 ----
        void CheckBool(string name, bool actual, bool expected)
        {
            if (actual == expected) { pass++; Console.WriteLine($"  PASS {name}"); }
            else { fail++; Console.WriteLine($"  FAIL {name} (期望 {expected}, 实际 {actual})"); }
        }

        void CheckCfg(string url, bool expected, AppSettings target)
        {
            var ok = UrlFilter.IsAllowed(new Uri(url), target);
            if (ok == expected) { pass++; Console.WriteLine($"  PASS {url} -> {ok}"); }
            else { fail++; Console.WriteLine($"  FAIL {url} -> {ok} (期望 {expected})"); }
        }

        Console.WriteLine("== 白名单应放行 ==");
        Check("https://www.bilibili.com/video/BV1xx411c7mD", true);      // B站视频页
        Check("https://bilibili.com/", true);                              // 裸域
        Check("https://space.bilibili.com/12345", true);                   // 子域
        Check("https://chatgpt.com/c/abc-123", true);                      // 会话页
        Check("https://gemini.google.com/app", true);                      // Gemini 应用页
        Check("https://aistudio.google.com/prompts/new_chat", true);       // Google AI Studio（用户实际入口）
        Check("https://aistudio.google.com/apps", true);                   // AI Studio 应用页
        Check("https://chat.deepseek.com/a/chat", true);                   // DeepSeek 会话
        Check("https://accounts.google.com/o/oauth2/auth?x=1", true);      // OAuth 登录
        Check("https://passport.bilibili.com/login", true);                // B站扫码登录
        Check("https://auth.openai.com/authorize", true);                  // ChatGPT 登录
        Check("https://notebook.google.com/", true);                       // Notebook 新入口
        Check("https://notebooklm.google.com/", true);                    // Notebook 旧入口兼容

        Console.WriteLine("== 非白名单应拦截 ==");
        Check("https://www.baidu.com/s?wd=1", false);                      // 搜索引擎
        Check("https://www.youtube.com/watch?v=x", false);                 // YouTube
        Check("https://weibo.com/u/123", false);                           // 微博
        Check("https://evil-bilibili.com/video/1", false);                 // 前缀仿冒域
        Check("https://bilibili.com.evil.io/video/1", false);              // 后缀仿冒域
        Check("https://notbilibili.com/", false);                          // 相似域
        
        // file:// 学习目录子树（PDF/图片/TXT 内置查看路径）—— v0.3.2 修复
        Check("file:///D:/%E6%9D%82%E6%96%87%E4%BB%B6/focus/%E8%AE%B2%E4%B9%89.pdf", true);
        Check("file:///C:/Windows/system32/config", false);                // 本地文件协议
        Check("javascript:alert(1)", false);                               // JS 协议
        Check("https://taobao.com", false);                                // 购物

        // v1.0.5：已配置用户的 sites[] 也必须从 preset 目录获得新域名，不能只依赖旧 config 白名单。
        var notebookCfg = new AppSettings
        {
            Sites = new List<SiteCatalog.SiteEntry> { new() { Id = "notebooklm" } }
        };
        SiteCatalog.ComputeEffectiveDomains(notebookCfg, out var notebookWl, out var notebookLd);
        notebookCfg.Whitelist = notebookWl;
        notebookCfg.LoginDomains = notebookLd;
        CheckCfg("https://notebook.google.com/", true, notebookCfg);
        CheckCfg("https://notebooklm.google.com/", true, notebookCfg);

        // ==================== T3：SiteCatalog 数据驱动站点自测（均 new AppSettings() 现场构造） ====================

        Console.WriteLine("== 组1 SiteCatalog.ResolveSites ==");
        {
            // 默认 cfg（Sites=null）→ 5 个 preset，id 顺序 bili/chatgpt/gemini/deepseek/notebooklm，全 IsPreset、AllowMulti
            // （"gemini" 为 v0.5.4 历史 id，经 BuiltInSites 别名解析到 AI Studio 定义；
            //  v1.0.3 notebooklm 升第 5 preset——用户 2026-09-03 指示）
            var g1 = new AppSettings();
            var r1 = SiteCatalog.ResolveSites(g1);
            CheckBool("ResolveSites: 默认返回 5 站", r1.Count == 5, true);
            CheckBool("ResolveSites: id 顺序 bili/chatgpt/gemini/deepseek/notebooklm (实际: " + string.Join(",", r1.Select(s => s.TabKey)) + ")",
                string.Join(",", r1.Select(s => s.TabKey)) == "bili,chatgpt,gemini,deepseek,notebooklm", true);
            CheckBool("ResolveSites: 全部 IsPreset", r1.All(s => s.IsPreset), true);
            CheckBool("ResolveSites: 全部 AllowMulti", r1.All(s => s.AllowMulti), true);
        }
        {
            // 只列 preset id → 定义来自代码目录（Title 以代码为准，config 只存 id）
            var g1 = new AppSettings();
            g1.Sites = new List<SiteCatalog.SiteEntry>
            {
                new() { Id = "bili" },
                new() { Id = "deepseek" },
            };
            var r1 = SiteCatalog.ResolveSites(g1);
            CheckBool("ResolveSites: 仅列 2 个 id → 恰好 2 站", r1.Count == 2, true);
            CheckBool("ResolveSites: preset Title 来自代码目录 (哔哩哔哩/DeepSeek)",
                r1.Count == 2 && r1[0].Title == "哔哩哔哩" && r1[1].Title == "DeepSeek", true);
        }
        {
            // 未知 id → 该条跳过不炸
            var g1 = new AppSettings();
            g1.Sites = new List<SiteCatalog.SiteEntry>
            {
                new() { Id = "unknown-x" },
                new() { Id = "bili" },
            };
            var r1 = SiteCatalog.ResolveSites(g1);
            CheckBool("ResolveSites: 未知 id 跳过不炸", r1.Count == 1 && r1[0].Id == "bili", true);
        }
        {
            // 重复 id（bili 两次）→ 只出现一次
            var g1 = new AppSettings();
            g1.Sites = new List<SiteCatalog.SiteEntry>
            {
                new() { Id = "bili" },
                new() { Id = "bili" },
            };
            CheckBool("ResolveSites: 重复 id 只出现一次", SiteCatalog.ResolveSites(g1).Count == 1, true);
        }
        {
            // custom 自含条目进入结果；缺 Domains / 缺 Url 的畸形条目跳过
            var g1 = new AppSettings();
            g1.Sites = new List<SiteCatalog.SiteEntry>
            {
                new() { Id = "site", Title = "Notion", Url = "https://notion.so", Domains = new() { "notion.so" } },
                new() { Id = "bad1", Title = "缺Domains", Url = "https://bad1.example.com" },
                new() { Id = "bad2", Title = "缺Url", Domains = new() { "bad2.example.com" } },
            };
            var r1 = SiteCatalog.ResolveSites(g1);
            CheckBool("ResolveSites: custom 出现且畸形条目跳过", r1.Count == 1 && r1[0].Id == "site", true);
            CheckBool("ResolveSites: custom AllowMulti=false", r1.Count == 1 && r1[0].AllowMulti == false, true);
            CheckBool("ResolveSites: custom IsPreset=false", r1.Count == 1 && r1[0].IsPreset == false, true);
        }

        Console.WriteLine("== 组2 SiteCatalog.ComputeEffectiveDomains ==");
        {
            // 默认 cfg → 白名单/登录域基线
            var g2 = new AppSettings();
            SiteCatalog.ComputeEffectiveDomains(g2, out var wl2, out var ld2);
            CheckBool("ComputeEffectiveDomains: whitelist 含 bilibili.com/chatgpt.com/deepseek.com/aistudio.google.com",
                wl2.Contains("bilibili.com") && wl2.Contains("chatgpt.com")
                    && wl2.Contains("deepseek.com") && wl2.Contains("aistudio.google.com"), true);
            CheckBool("ComputeEffectiveDomains: loginDomains 含 accounts.google.com",
                ld2.Contains("accounts.google.com"), true);
            CheckBool("ComputeEffectiveDomains: loginDomains 含 passport.bilibili.com",
                ld2.Contains("passport.bilibili.com"), true);
            CheckBool("ComputeEffectiveDomains: loginDomains 含 auth.openai.com",
                ld2.Contains("auth.openai.com"), true);
        }
        {
            // 用户 custom 域撞 preset 登录域（custom Domains=["accounts.google.com"]）→ whitelist 优先
            var g2 = new AppSettings();
            g2.Sites = new List<SiteCatalog.SiteEntry>
            {
                new() { Id = "aistudio" },
                new() { Id = "site", Title = "Custom", Url = "https://accounts.google.com", Domains = new() { "accounts.google.com" } },
            };
            SiteCatalog.ComputeEffectiveDomains(g2, out var wl2, out var ld2);
            CheckBool("ComputeEffectiveDomains: 冲突域在 whitelist 中", wl2.Contains("accounts.google.com"), true);
            CheckBool("ComputeEffectiveDomains: 冲突域不在 loginDomains 中 (whitelist 优先)",
                !ld2.Contains("accounts.google.com"), true);
        }
        {
            // legacy 形状（Sites=null 且手工改过 cfg.Whitelist）→ 原值保留
            var g2 = new AppSettings();
            g2.Whitelist.Add("example.com");
            SiteCatalog.ComputeEffectiveDomains(g2, out var wl2, out _);
            CheckBool("ComputeEffectiveDomains: legacy 自定义域保留 (example.com)", wl2.Contains("example.com"), true);
        }

        Console.WriteLine("== 组3 SiteCatalog.ParseCustomInput + UrlFilter 联动 ==");
        var g3 = new AppSettings();
        var notion = SiteCatalog.ParseCustomInput("notion.so", null, g3);
        CheckBool("ParseCustomInput: notion.so 无 scheme 成功", notion != null, true);
        if (notion != null)
        {
            CheckBool("ParseCustomInput: Domains==[notion.so]",
                notion.Domains != null && notion.Domains.Count == 1 && notion.Domains[0] == "notion.so", true);
            CheckBool("ParseCustomInput: Url 以 https:// 开头",
                notion.Url != null && notion.Url.StartsWith("https://", StringComparison.Ordinal), true);
            CheckBool("ParseCustomInput: 标题留空 → Title==host", notion.Title == "notion.so", true);
        }
        var www = SiteCatalog.ParseCustomInput("https://www.Example.COM/path?q=1", "Example", g3);
        CheckBool("ParseCustomInput: www 去除+小写 → Domains==[example.com]",
            www != null && www.Domains != null && www.Domains.Count == 1 && www.Domains[0] == "example.com", true);
        CheckBool("ParseCustomInput: ftp:// 拒绝", SiteCatalog.ParseCustomInput("ftp://x.com", null, g3) == null, true);
        CheckBool("ParseCustomInput: 非 URL 拒绝", SiteCatalog.ParseCustomInput("not a url", null, g3) == null, true);
        CheckBool("ParseCustomInput: IP 字面量拒绝", SiteCatalog.ParseCustomInput("http://192.168.1.1/", null, g3) == null, true);
        var g3c = new AppSettings();
        CheckBool("ParseCustomInput: 撞 preset 白名单子域 → null",
            SiteCatalog.ParseCustomInput("https://sub.bilibili.com", null, g3c) == null, true);
        CheckBool("ParseCustomInput: 撞登录域 → null",
            SiteCatalog.ParseCustomInput("https://accounts.google.com", null, g3c) == null, true);
        if (notion != null)
        {
            // 先把 notion.so 加进 cfg.Sites（ParseCustomInput 不会自动 Add，调用方负责）
            g3.Sites!.Add(notion);
            CheckBool("ParseCustomInput: URL 重复 → null",
                SiteCatalog.ParseCustomInput("https://notion.so", "Notion 2", g3) == null, true);
            // 联动：Sites → ComputeEffectiveDomains → UrlFilter
            SiteCatalog.ComputeEffectiveDomains(g3, out var wl3, out var ld3);
            g3.Whitelist = wl3;
            g3.LoginDomains = ld3;
            CheckCfg("https://www.notion.so/page", true, g3);
            CheckCfg("https://evil-notion.so", false, g3);
        }
        CheckBool("AllocateCustomId: [site,site-2] → site-3",
            SiteCatalog.AllocateCustomId(new[] { "site", "site-2" }) == "site-3", true);

        Console.WriteLine($"== 结果: {pass} pass / {fail} fail ==");
        return fail == 0 ? 0 : 1;
    }
}
