using System.Management;

namespace focus_desktop.Services;

internal sealed record BrightnessState(int? Percent, string? Error);

internal sealed class BrightnessService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal Task<BrightnessState> ReadAsync() => ExecuteAsync(null);
    internal Task<BrightnessState> SetAsync(int percent) => ExecuteAsync(Math.Clamp(percent, 0, 100));

    private async Task<BrightnessState> ExecuteAsync(int? percent)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var query = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE");
                    using var monitors = query.Get();
                    foreach (ManagementObject monitor in monitors)
                    {
                        using (monitor)
                        {
                            var instance = (string)monitor["InstanceName"];
                            if (percent is { } value)
                            {
                                using var methodsQuery = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
                                using var methods = methodsQuery.Get();
                                var written = false;
                                foreach (ManagementObject method in methods)
                                {
                                    using (method)
                                    {
                                        if (!string.Equals((string)method["InstanceName"], instance, StringComparison.OrdinalIgnoreCase)) continue;
                                        var result = Convert.ToUInt32(method.InvokeMethod("WmiSetBrightness", new object[] { 0u, (byte)value }));
                                        if (result != 0) throw new InvalidOperationException($"亮度写入失败（{result}）");
                                        written = true;
                                    }
                                }
                                if (!written) return new(null, "当前屏幕没有可用的亮度写入接口");
                                monitor.Get();
                            }
                            return new BrightnessState(Convert.ToInt32(monitor["CurrentBrightness"]), null);
                        }
                    }
                    return new BrightnessState(null, "当前屏幕不支持内屏亮度调节（外接屏暂不支持）");
                }
                catch (Exception ex) { return new BrightnessState(null, $"亮度不可用：{ex.Message}"); }
            });
        }
        finally { _gate.Release(); }
    }
}
