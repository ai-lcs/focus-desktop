using System.IO;
using System.Text;

namespace FocusDesktop;

/// <summary>
/// 崩溃日志（Umbra.Core/CrashReporter.cs 移植，MIT）。
/// 自身永不抛异常；最多保留 20 份。
/// </summary>
public static class CrashReporter
{
    private const int MaxCrashLogs = 20;

    public static string? Write(Exception exception, string source)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var ts = DateTimeOffset.Now;
            var path = Path.Combine(AppPaths.LogDir,
                $"crash-{ts:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            var sb = new StringBuilder()
                .AppendLine("focus-desktop crash report")
                .AppendLine($"Time: {ts:O}")
                .AppendLine($"Source: {source}")
                .AppendLine($"Version: {typeof(CrashReporter).Assembly.GetName().Version}")
                .AppendLine($"Process: {Environment.ProcessId}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine()
                .AppendLine(exception.ToString());
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            PruneOldLogs();
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static void Note(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            File.AppendAllText(Path.Combine(AppPaths.LogDir, "events.log"),
                $"{DateTimeOffset.Now:O} {message}\n", Encoding.UTF8);
        }
        catch { }
    }

    private static void PruneOldLogs()
    {
        try
        {
            foreach (var f in new DirectoryInfo(AppPaths.LogDir)
                         .EnumerateFiles("crash-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(MaxCrashLogs))
            {
                f.Delete();
            }
        }
        catch { }
    }
}
