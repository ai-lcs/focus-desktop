using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace focus_desktop.Services;

/// <summary>
/// 应用配置（config.json）。读写全部走 AtomicFile。
/// Step 3 起：白名单/登录域从此读取；Step 6 起：退出语从此读取。
/// </summary>
public class AppSettings
{
    public enum ConfigLoadStatus { Missing, Valid, Corrupt }

    [JsonPropertyName("studyFolder")]
    public string StudyFolder { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "focus");

    [JsonPropertyName("exitPhrase")]
    public string ExitPhrase { get; set; } = "我发誓我确实有事需要离开这个环境，我要马上回来。";

    /// <summary>顶层导航白名单（站点主域）。</summary>
    [JsonPropertyName("whitelist")]
    public List<string> Whitelist { get; set; } = new()
    {
        "chatgpt.com",
        "gemini.google.com",
        "aistudio.google.com",
        "deepseek.com",
        "bilibili.com",
        "notebook.google.com",
        "notebooklm.google.com",
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

    // ---- 番茄钟（高度自定义，PomodoroService 读取） ----
    [JsonPropertyName("pomodoroWorkMinutes")]
    public int? PomodoroWorkMinutes { get; set; }

    [JsonPropertyName("pomodoroShortBreakMinutes")]
    public int? PomodoroShortBreakMinutes { get; set; }

    [JsonPropertyName("pomodoroLongBreakMinutes")]
    public int? PomodoroLongBreakMinutes { get; set; }

    [JsonPropertyName("pomodoroCyclesUntilLong")]
    public int? PomodoroCyclesUntilLong { get; set; }

    /// <summary>首页专注语（一句让自己专注的话，config 可改）。</summary>
    [JsonPropertyName("focusQuote")]
    public string FocusQuote { get; set; } = "你想成为怎样的人？";

    // ---- Public v1：schema 版本 / 冻结标志 / 站点集 / 背景图（向导写入，见 SiteCatalog） ----
    /// <summary>配置 schema 版本。缺失=legacy v1（v0.5.x 老配置）。</summary>
    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; set; }

    /// <summary>首次向导已完成冻结标志。null/false=未完成（进向导）；true=配置已冻结。</summary>
    [JsonPropertyName("configured")]
    public bool? Configured { get; set; }

    /// <summary>站点集（preset 存 id；custom 自含 title/url/domains）。null/空=legacy → 全 preset。</summary>
    [JsonPropertyName("sites")]
    public List<SiteCatalog.SiteEntry>? Sites { get; set; }

    /// <summary>首页背景图文件名（DataDir/assets/ 下；仅文件名不存路径）。</summary>
    [JsonPropertyName("backgroundImage")]
    public string? BackgroundImage { get; set; }

    /// <summary>瞬态实例（预览草稿）：Save() 拒绝落盘。仅内存态，不序列化。</summary>
    [JsonIgnore]
    public bool IsTransient { get; set; }

    /// <summary>是否已完成首次配置（冻结）。判据只认显式 true：旧配置缺失该字段视为未完成。</summary>
    public bool IsConfigured() => Configured == true;

    /// <summary>legacy v1 配置（有实质旧字段但无 schemaVersion）。升级后应保持原有行为。</summary>
    public bool IsLegacyConfig() => SchemaVersion == null && Configured == null && Whitelist.Count > 0;

    public static bool Exists() => File.Exists(Paths.ConfigFile);

    public static AppSettings LoadOrDefault() => LoadOrDefault(out _);

    public static AppSettings LoadOrDefault(out ConfigLoadStatus status)
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile))
            {
                status = ConfigLoadStatus.Missing;
                return new AppSettings();
            }

            var cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.ConfigFile));
            // JSON 语法正确但关键集合被显式写成 null，同样不能当作可运行配置。
            if (cfg != null && cfg.Whitelist != null && cfg.LoginDomains != null
                && cfg.StudyFolder != null && cfg.ExitPhrase != null && cfg.FocusQuote != null)
            {
                status = ConfigLoadStatus.Valid;
                return cfg;
            }
        }
        catch
        {
            // 继续走 Corrupt 分支：启动层会隔离原文件并进入向导，不直接锁定。
        }
        status = ConfigLoadStatus.Corrupt;
        return new AppSettings { Configured = false };
    }

    /// <summary>隔离损坏配置，保留原文件供人工恢复；失败时不覆盖原文件。</summary>
    public static string? QuarantineCorruptConfig()
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile)) return null;
            var backup = $"{Paths.ConfigFile}.corrupt-{DateTime.Now:yyyyMMdd-HHmmssfff}.json";
            File.Move(Paths.ConfigFile, backup);
            return backup;
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        if (IsTransient) return; // 预览草稿等瞬态实例：绝不落盘（真预览链路 T5 契约）
        Paths.EnsureDirectories();
        AtomicFile.WriteAllText(Paths.ConfigFile,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
