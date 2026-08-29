using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusDesktop;

/// <summary>应用配置（config.json）。读写全部走 AtomicFile。</summary>
public class AppSettings
{
    [JsonPropertyName("studyFolder")]
    public string StudyFolder { get; set; } = @"D:\Study";

    [JsonPropertyName("exitPhrase")]
    public string ExitPhrase { get; set; } = "我确实有事需要离开这个环境，我要马上回来。";

    /// <summary>顶层导航白名单（站点主域）。</summary>
    [JsonPropertyName("whitelist")]
    public List<string> Whitelist { get; set; } = new()
    {
        "chatgpt.com",
        "gemini.google.com",
        "deepseek.com",
        "bilibili.com",
    };

    /// <summary>登录过程放行域（OAuth/扫码）。实测后增删。</summary>
    [JsonPropertyName("loginDomains")]
    public List<string> LoginDomains { get; set; } = new()
    {
        "accounts.google.com",
        "auth.openai.com",
        "cdn.auth0.com",
        "passport.bilibili.com",
        "login.live.com",
    };

    private static string SettingsPath =>
        Path.Combine(AppPaths.DataDir, "config.json");

    public static AppSettings LoadOrDefault()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // 损坏的配置文件 → 回退默认值（Umbra Config 的容错模式）
        }
        return new AppSettings();
    }

    public void Save() => AtomicFile.WriteAllText(SettingsPath,
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>路径表（Umbra Config.cs 的精简版）。</summary>
public static class AppPaths
{
    /// <summary>数据目录：exe 旁的 DeskHubData/（便携布局，勿装进 Program Files）。</summary>
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "DeskHubData");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string FocusStateFile => Path.Combine(DataDir, "focus_state.json");
}
