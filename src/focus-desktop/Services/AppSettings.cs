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
    public enum ConfigLoadStatus { Missing, Valid, LegacyIdentityConflict, Corrupt }

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

    /// <summary>外部应用名单已由用户确认。null/null 保留为旧配置的待补充状态。</summary>
    [JsonPropertyName("externalAppsConfirmed")]
    public bool? ExternalAppsConfirmed { get; set; }

    /// <summary>冻结的外部应用入口；只有显式确认后才允许为空列表。</summary>
    [JsonPropertyName("externalApps")]
    public List<ExternalAppEntry>? ExternalApps { get; set; }

    /// <summary>瞬态实例（预览草稿）：Save() 拒绝落盘。仅内存态，不序列化。</summary>
    [JsonIgnore]
    public bool IsTransient { get; set; }

    /// <summary>是否已完成首次配置（冻结）。判据只认显式 true：旧配置缺失该字段视为未完成。</summary>
    public bool IsConfigured() => Configured == true;

    /// <summary>检查外部应用配置的三态与结构。运行时文件存在性由启动服务处理。</summary>
    public bool TryValidateExternalApps(out string? error)
        => TryValidateExternalApps(allowWindowIdentityConflicts: false, out error);

    private bool TryValidateExternalApps(bool allowWindowIdentityConflicts, out string? error)
    {
        error = null;
        if (ExternalAppsConfirmed == null && ExternalApps == null)
            return true; // 旧配置：尚未确认这一项

        if (ExternalAppsConfirmed != true || ExternalApps == null)
        {
            error = "外部应用名单确认状态与名单内容不一致。";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orders = new HashSet<int>();
        var validEntries = new List<ExternalAppEntry>();
        foreach (var entry in ExternalApps)
        {
            if (entry == null)
            {
                error = "外部应用名单中包含空入口。";
                return false;
            }
            if (!entry.TryValidate(out error))
                return false;
            if (!ids.Add(entry.Id))
            {
                error = "外部应用名单中有重复的入口标识。";
                return false;
            }
            if (!orders.Add(entry.Order))
            {
                error = "外部应用名单中有重复的顺序号。";
                return false;
            }

            var identityConflict = ExternalAppEntry.FindWindowMatchIdentityConflict(validEntries, entry);
            if (identityConflict != null && !allowWindowIdentityConflicts)
            {
                error = ExternalAppEntry.DescribeWindowMatchIdentityConflict(identityConflict, entry);
                return false;
            }
            validEntries.Add(entry);
        }

        return true;
    }

    public string? GetExternalAppIdentityConflictMessage()
    {
        if (ExternalApps == null) return null;
        var validEntries = new List<ExternalAppEntry>();
        foreach (var entry in ExternalApps)
        {
            if (entry == null) continue;
            var conflict = ExternalAppEntry.FindWindowMatchIdentityConflict(validEntries, entry);
            if (conflict != null)
                return ExternalAppEntry.DescribeWindowMatchIdentityConflict(conflict, entry);
            validEntries.Add(entry);
        }
        return null;
    }

    /// <summary>legacy v1 配置（有实质旧字段但无 schemaVersion）。升级后应保持原有行为。</summary>
    public bool IsLegacyConfig() => SchemaVersion == null && Configured == null && Whitelist.Count > 0;

    public static bool Exists() => File.Exists(Paths.ConfigFile);

    public static AppSettings LoadOrDefault() => LoadOrDefault(out _);

    public static AppSettings LoadOrDefault(out ConfigLoadStatus status) =>
        LoadFromPath(Paths.ConfigFile, out status);

    internal static AppSettings LoadFromPath(string configPath, out ConfigLoadStatus status)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                status = ConfigLoadStatus.Missing;
                return new AppSettings();
            }

            var cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(configPath));
            // JSON 语法正确但关键集合被显式写成 null，同样不能当作可运行配置。
            if (cfg != null && cfg.Whitelist != null && cfg.LoginDomains != null
                && cfg.StudyFolder != null && cfg.ExitPhrase != null && cfg.FocusQuote != null
                && cfg.TryValidateExternalApps(allowWindowIdentityConflicts: true, out _))
            {
                status = cfg.TryValidateExternalApps(out _)
                    ? ConfigLoadStatus.Valid
                    : ConfigLoadStatus.LegacyIdentityConflict;
                return cfg;
            }
        }
        catch
        {
            // 继续走 Corrupt 分支：启动层提示后退出；不隔离或改写原文件。
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
        if (IsTransient) return;
        Paths.EnsureDirectories();
        SaveToPath(Paths.ConfigFile);
    }

    internal void SaveToPath(string configPath)
    {
        if (IsTransient) return; // 预览草稿等瞬态实例：绝不落盘（真预览链路 T5 契约）
        if (!TryValidateExternalApps(out var error))
            throw new InvalidDataException(error);
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(configPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
