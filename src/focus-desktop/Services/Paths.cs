namespace focus_desktop.Services;

/// <summary>路径约定（便携布局：所有运行数据在 exe 旁的 focus-desktop-data/）。</summary>
public static class Paths
{
    public static readonly string BaseDir = AppContext.BaseDirectory;
    public static readonly string DataDir = Path.Combine(BaseDir, "focus-desktop-data");
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
