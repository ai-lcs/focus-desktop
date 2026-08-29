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
        int pass = 0, fail = 0;

        void Check(string url, bool expected)
        {
            var ok = UrlFilter.IsAllowed(new Uri(url), cfg);
            if (ok == expected) { pass++; Console.WriteLine($"  PASS {url} -> {ok}"); }
            else { fail++; Console.WriteLine($"  FAIL {url} -> {ok} (期望 {expected})"); }
        }

        Console.WriteLine("== 白名单应放行 ==");
        Check("https://www.bilibili.com/video/BV1xx411c7mD", true);      // B站视频页
        Check("https://bilibili.com/", true);                              // 裸域
        Check("https://space.bilibili.com/12345", true);                   // 子域
        Check("https://chatgpt.com/c/abc-123", true);                      // 会话页
        Check("https://gemini.google.com/app", true);                      // Gemini 应用页
        Check("https://chat.deepseek.com/a/chat", true);                   // DeepSeek 会话
        Check("https://accounts.google.com/o/oauth2/auth?x=1", true);      // OAuth 登录
        Check("https://passport.bilibili.com/login", true);                // B站扫码登录
        Check("https://auth.openai.com/authorize", true);                  // ChatGPT 登录

        Console.WriteLine("== 非白名单应拦截 ==");
        Check("https://www.baidu.com/s?wd=1", false);                      // 搜索引擎
        Check("https://www.youtube.com/watch?v=x", false);                 // YouTube
        Check("https://weibo.com/u/123", false);                           // 微博
        Check("https://evil-bilibili.com/video/1", false);                 // 前缀仿冒域
        Check("https://bilibili.com.evil.io/video/1", false);              // 后缀仿冒域
        Check("https://notbilibili.com/", false);                          // 相似域
        Check("file:///C:/Windows/system32/config", false);                // 本地文件协议
        Check("javascript:alert(1)", false);                               // JS 协议
        Check("https://taobao.com", false);                                // 购物

        Console.WriteLine($"== 结果: {pass} pass / {fail} fail ==");
        return fail == 0 ? 0 : 1;
    }
}
