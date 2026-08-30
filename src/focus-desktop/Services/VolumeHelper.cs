using System.Runtime.InteropServices;

namespace focus_desktop.Services;

/// <summary>
/// 音量控制：Core Audio IAudioEndpointVolume interop。
/// v0.3.4 重写：旧实现 IMMDevice.Activate 用 out object + GUID 装箱，在 .NET 10 上
/// 抛 "Specified OLE variant is invalid"（录屏实证：音量从未真正工作过，UI 的 50 全是
/// 失败兜底假值）。新实现用声明正确的 COM 接口 + Marshal.GetObjectForNativeVariant 替代路径。
/// 失败不再静默：IsReady/LastError 暴露给 --voltest 诊断。
/// </summary>
public static class VolumeHelper
{
    private static IAudioEndpointVolume? _vol;
    private static bool _tried;

    /// <summary>COM 通道是否就绪。false 时 UI 的 50 是假值（Get 失败兜底）。</summary>
    public static bool IsReady => _vol != null;

    /// <summary>初始化/调用失败原因（诊断用）。</summary>
    public static string? LastError { get; private set; }

    public static void Init()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            var devEnum = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            var hr = devEnum.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev);
            if (hr != 0 || dev == null)
            {
                LastError = $"GetDefaultAudioEndpoint hr=0x{hr:X8}";
                return;
            }
            var iid = typeof(IAudioEndpointVolume).GUID;
            hr = dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out var obj);
            if (hr != 0 || obj == null)
            {
                LastError = $"Activate hr=0x{hr:X8}";
                return;
            }
            _vol = (IAudioEndpointVolume)obj;
            LastError = null;
        }
        catch (Exception ex)
        {
            _vol = null;
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public static int Get()
    {
        if (_vol == null) return 50;
        try { _vol.GetMasterVolumeLevelScalar(out var f); return (int)Math.Round(f * 100); }
        catch (Exception ex) { LastError = $"Get: {ex.Message}"; return 50; }
    }

    public static void Set(int percent)
    {
        if (_vol == null) return;
        try { _vol.SetMasterVolumeLevelScalar(percent / 100f, IntPtr.Zero); }
        catch (Exception ex) { LastError = $"Set: {ex.Message}"; }
    }

    public static bool IsMuted()
    {
        if (_vol == null) return false;
        try { _vol.GetMute(out var m); return m; }
        catch (Exception ex) { LastError = $"GetMute: {ex.Message}"; return false; }
    }

    /// <summary>切换静音；返回切换后是否处于静音态。</summary>
    public static bool ToggleMute()
    {
        if (_vol == null) return false;
        try
        {
            _vol.GetMute(out var m);
            _vol.SetMute(!m, IntPtr.Zero);
            return !m;
        }
        catch (Exception ex) { LastError = $"ToggleMute: {ex.Message}"; return false; }
    }

    // ---- COM interop（vtable 顺序敏感：IAudioEndpointVolume 完整 12 方法声明） ----
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        // vtable：IUnknown(3) 之后第一个自定义方法就是 Activate（GetId/GetState 在
        // IMMDevice 的真实定义里位于 Activate 之后——之前臆造前置导致 vtable 错位，obj=null）
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out int channels);
        int SetMasterVolumeLevel(float db, IntPtr ctx);
        int SetMasterVolumeLevelScalar(float level, IntPtr ctx);
        int GetMasterVolumeLevel(out float db);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(int channel, float db, IntPtr ctx);
        int SetChannelVolumeLevelScalar(int channel, float level, IntPtr ctx);
        int GetChannelVolumeLevel(int channel, out float db);
        int GetChannelVolumeLevelScalar(int channel, out float level);
        int SetMute(bool mute, IntPtr ctx);
        int GetMute(out bool mute);
    }
}
