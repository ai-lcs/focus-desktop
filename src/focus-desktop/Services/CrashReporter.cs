using System.Text;

using System.IO;
namespace focus_desktop.Services;

/// <summary>
/// 崩溃日志（Umbra.Core/CrashReporter.cs 模式，MIT）。
/// 自身绝不抛异常——崩溃报告写失败不能再引发新崩溃。
/// </summary>
public static class CrashReporter
{
    private const int MaxCrashLogs = 20;

    public static string? Write(Exception exception, string source, string? appVersion = null)
    {
        try
        {
            Paths.EnsureDirectories();
            var timestamp = DateTimeOffset.Now;
            var path = Path.Combine(Paths.LogsDir,
                $"crash-{timestamp:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            var contents = new StringBuilder()
                .AppendLine("focus-desktop crash report")
                .AppendLine($"Time: {timestamp:O}")
                .AppendLine($"Source: {source}")
                .AppendLine($"Version: {appVersion ?? "0.1.0"}")
                .AppendLine($"Process: {Environment.ProcessId}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();
            File.WriteAllText(path, contents, Encoding.UTF8);
            PruneOldLogs();
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            foreach (var file in new DirectoryInfo(Paths.LogsDir)
                         .EnumerateFiles("crash-*.log")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(MaxCrashLogs))
            {
                file.Delete();
            }
        }
        catch
        {
            // 清理失败无所谓
        }
    }
}
