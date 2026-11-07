using System.IO;
namespace focus_desktop.Services;

/// <summary>
/// 路径约定（双布局，Public v1）：
/// - Portable（Kevin 自用/zip 发布）：exe 旁 portable.flag 存在 → 数据在 exe 旁 focus-desktop-data/
///   ——与 v0.5.4 及之前逐字节一致（测试脚本/清孤儿 webview 过滤串/watchdog 全依赖此布局）；
/// - Installed（新版 Dock）：无 portable.flag → %LOCALAPPDATA%\focus-desktop-dock。
///   不探测、迁移或修改旧版 focus-desktop 数据。便携版仍使用各自目录内的数据。
/// 检测机制 = 文件存在性，无注册表/编译开关，两种产物出自同一份二进制。
/// 判定在进程内只做一次（静态字段初始化）；运行中不得变化。
/// </summary>
public static class Paths
{
    public const string InstalledDataDirectoryName = "focus-desktop-dock";
    public static readonly string BaseDir = AppContext.BaseDirectory;

    /// <summary>Portable 布局标记文件（zip 发布时放置；Inno 安装包不含）。</summary>
    private static readonly string PortableFlag = Path.Combine(BaseDir, "portable.flag");

    public static readonly string DataDir =
        File.Exists(PortableFlag)
            ? Path.Combine(BaseDir, "focus-desktop-data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstalledDataDirectoryName);

    public static readonly string LogsDir = Path.Combine(DataDir, "logs");
    public static readonly string ConfigFile = Path.Combine(DataDir, "config.json");
    public static readonly string SessionStateFile = Path.Combine(DataDir, "session_state.json");
    public static readonly string SmokeLog = Path.Combine(DataDir, "smoke.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
