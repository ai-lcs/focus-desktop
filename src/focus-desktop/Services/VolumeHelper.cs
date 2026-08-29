using System.Runtime.InteropServices;

namespace focus_desktop.Services;

/// <summary>
/// 音量控制：Core Audio IAudioEndpointVolume 精简 interop。
/// 只做主音量 get/set（spec §11 要求"简单音量调整"）。
/// </summary>
public static class VolumeHelper
{
    private static IAudioEndpointVolume? _vol;
    private static bool _tried;

    public static void Init()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            var devEnum = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            devEnum.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev);
            dev.Activate(typeof(IAudioEndpointVolume).GUID, 1 /*CLSCTX_INPROC_SERVER*/,
                IntPtr.Zero, out var obj);
            _vol = (IAudioEndpointVolume)obj;
        }
        catch
        {
            _vol = null; // 无音频设备时静默降级（Slider 仍显示但不生效）
        }
    }

    public static int Get()
    {
        if (_vol == null) return 50;
        try { _vol.GetMasterVolumeLevelScalar(out var f); return (int)Math.Round(f * 100); }
        catch { return 50; }
    }

    public static void Set(int percent)
    {
        if (_vol == null) return;
        try { _vol.SetMasterVolumeLevelScalar(percent / 100f, IntPtr.Zero); }
        catch { }
    }

    // ---- COM interop ----
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out object iface);
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
