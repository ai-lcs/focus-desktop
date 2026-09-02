using System.IO;
namespace focus_desktop.Services;

/// <summary>
/// 路径约定（双布局，Public v1）：
/// - Portable（Kevin 自用/zip 发布）：exe 旁 portable.flag 存在 → 数据在 exe 旁 focus-desktop-data/
///   ——与 v0.5.4 及之前逐字节一致（测试脚本/清孤儿 webview 过滤串/watchdog 全依赖此布局）；
/// - Installed（Inno Setup 装机）：无 portable.flag → %LOCALAPPDATA%\focus-desktop
///   （Program Files 不可写；目录名保持 focus-desktop，清孤儿脚本过滤串通用）。
/// 检测机制 = 文件存在性，无注册表/编译开关，两种产物出自同一份二进制。
/// 判定在进程内只做一次（静态字段初始化）；运行中不得变化。
/// </summary>
public static class Paths
{
    public static readonly string BaseDir = AppContext.BaseDirectory;

    /// <summary>Portable 布局标记文件（zip 发布时放置；Inno 安装包不含）。</summary>
    private static readonly string PortableFlag = Path.Combine(BaseDir, "portable.flag");

    public static readonly string DataDir =
        File.Exists(PortableFlag)
            ? Path.Combine(BaseDir, "focus-desktop-data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "focus-desktop");

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
