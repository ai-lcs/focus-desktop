using System.Runtime.InteropServices;

namespace focus_desktop.Services;

/// <summary>Current default-device volume and verified mute for all active playback outputs.</summary>
public static class VolumeHelper
{
    public static bool IsReady { get; private set; }
    public static string? LastError { get; private set; }
    internal sealed record EndpointState(string Id, int Volume, bool Muted);

    public static void Init() => TryRead(out _, out _);

    // Resolve current endpoints on every operation: a cached endpoint can remain valid
    // after Windows switches playback to a headset or another output.
    private static T WithDefault<T>(Func<IAudioEndpointVolume, T> action)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null;
        object? endpoint = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device!));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 1, IntPtr.Zero, out endpoint!));
            return action((IAudioEndpointVolume)endpoint);
        }
        finally { Release(endpoint); Release(device); Release(enumerator); }
    }

    private static void ForEachOutput(Action<string, IAudioEndpointVolume> action)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection? devices = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out devices!));
            devices.GetCount(out var count);
            if (count == 0) throw new InvalidOperationException("没有可用的声音输出设备");
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                object? endpoint = null;
                try
                {
                    devices.Item(i, out device!);
                    Marshal.ThrowExceptionForHR(device.GetId(out var id));
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(ref iid, 1, IntPtr.Zero, out endpoint!));
                    action(id, (IAudioEndpointVolume)endpoint);
                }
                finally { Release(endpoint); Release(device); }
            }
        }
        finally { Release(devices); Release(enumerator); }
    }

    internal static List<EndpointState> CaptureState()
    {
        var states = new List<EndpointState>();
        ForEachOutput((id, volume) =>
        {
            volume.GetMasterVolumeLevelScalar(out var level);
            volume.GetMute(out var muted);
            states.Add(new(id, (int)Math.Round(level * 100), muted));
        });
        return states;
    }

    internal static void RestoreState(List<EndpointState> states) => ForEachOutput((id, volume) =>
    {
        var state = states.Find(item => item.Id == id);
        if (state != null) volume.SetMute(state.Muted, IntPtr.Zero);
    });

    public static bool TryRead(out int percent, out bool muted)
    {
        percent = 0;
        muted = false;
        try
        {
            percent = WithDefault(volume => { volume.GetMasterVolumeLevelScalar(out var level); return (int)Math.Round(level * 100); });
            muted = CaptureState().All(state => state.Muted);
            IsReady = true;
            LastError = null;
            return true;
        }
        catch (Exception ex) { IsReady = false; LastError = ex.Message; return false; }
    }

    public static int Get() => TryRead(out var value, out _) ? value : 0;
    public static bool IsMuted() => TryRead(out _, out var muted) && muted;

    public static void Set(int percent)
    {
        try
        {
            WithDefault(volume => { volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, IntPtr.Zero); return true; });
            LastError = null;
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public static bool ToggleMute()
    {
        if (!TryRead(out _, out var muted)) return false;
        var desired = !muted;
        try
        {
            ForEachOutput((_, volume) => volume.SetMute(desired, IntPtr.Zero));
            if (CaptureState().Any(state => state.Muted != desired))
                throw new InvalidOperationException("部分输出设备未完成静音切换，请重试");
            LastError = null;
            return desired;
        }
        catch (Exception ex) { LastError = ex.Message; return muted; }
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    // COM declarations preserve native vtable order through the last method used.
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        // OpenPropertyStore must precede GetId even though it is not called here.
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out int channels);
        void SetMasterVolumeLevel(float db, IntPtr ctx);
        void SetMasterVolumeLevelScalar(float level, IntPtr ctx);
        void GetMasterVolumeLevel(out float db);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(int channel, float db, IntPtr ctx);
        void SetChannelVolumeLevelScalar(int channel, float level, IntPtr ctx);
        void GetChannelVolumeLevel(int channel, out float db);
        void GetChannelVolumeLevelScalar(int channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
