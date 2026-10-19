using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FocusDesk.DockTestFixture;

public partial class App : Application
{
    private FixtureOptions _options = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _options = FixtureOptions.Parse(e.Args);

        if (_options.StartDelayMilliseconds > 0)
            await Task.Delay(_options.StartDelayMilliseconds);

        if (_options.SpawnChild)
            StartChild();

        for (var i = 1; i <= _options.WindowCount; i++)
            new FixtureWindow(_options, i).Show();
    }

    private void StartChild()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Cannot resolve fixture executable path.");

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--fixture-child");
        startInfo.ArgumentList.Add("--fixture-owner-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("--fixture-report-dir");
        startInfo.ArgumentList.Add(_options.ReportDirectory);
        startInfo.ArgumentList.Add("--fixture-start-delay-ms");
        startInfo.ArgumentList.Add("0");
        Process.Start(startInfo);
    }
}

internal sealed record FixtureOptions(
    string ReportDirectory,
    int WindowCount,
    int StartDelayMilliseconds,
    int? OwnerProcessId,
    bool IsChild,
    bool SpawnChild,
    bool StartMinimized)
{
    public static FixtureOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--fixture-", StringComparison.OrdinalIgnoreCase))
                continue;

            if (key is "--fixture-child" or "--fixture-spawn-child" or "--fixture-start-minimized")
            {
                flags.Add(key);
                continue;
            }

            if (i + 1 < args.Length)
                values[key] = args[++i];
        }

        var reportDirectory = GetPath(values, "--fixture-report-dir") ??
                              Path.Combine(Path.GetTempPath(), "Focus Desk Dock Test Fixture");
        var windowCount = GetInt(values, "--fixture-window-count", 1, 1, 5);
        var delay = GetInt(values, "--fixture-start-delay-ms", 0, 0, 60000);
        int? ownerPid = int.TryParse(Get(values, "--fixture-owner-pid"), out var parsedOwner) ? parsedOwner : null;
        var isChild = flags.Contains("--fixture-child");
        var spawnChild = flags.Contains("--fixture-spawn-child") && !isChild;

        Directory.CreateDirectory(reportDirectory);
        return new FixtureOptions(reportDirectory, windowCount, delay, ownerPid, isChild, spawnChild,
            flags.Contains("--fixture-start-minimized"));
    }

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string? GetPath(Dictionary<string, string> values, string key)
    {
        var value = Get(values, key);
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }

    private static int GetInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!int.TryParse(Get(values, key), out var value))
            return fallback;
        return Math.Clamp(value, min, max);
    }
}
