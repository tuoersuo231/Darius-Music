using System.Runtime.InteropServices;

namespace CurrentLyrics.Services;

/// <summary>
/// Reads and writes this process's per-app volume in the Windows Volume Mixer
/// (IAudioSessionManager2 → ISimpleAudioVolume), so the value can be restored even when
/// Windows has no persisted entry for the executable.
/// </summary>
public static class AudioSession
{
    private const int CLSCTX_ALL = 23;
    private const int DEVICE_STATE_ACTIVE = 1;

    public static float? GetVolume(uint pid)
    {
        try
        {
            var manager = GetSessionManager();
            if (manager == null) return null;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);
            float? best = null;
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var control);
                try
                {
                    if (control is not IAudioSessionControl2 control2) continue;
                    control2.GetProcessId(out uint sessionPid);
                    if (sessionPid != pid) continue;
                    if (control is not ISimpleAudioVolume volume) continue;
                    control.GetState(out int state);
                    volume.GetMasterVolume(out float value);
                    // A track change can leave an old session behind; the live one is what the
                    // mixer shows, so prefer it.
                    if (state == 2) return value;
                    best ??= value;
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    public static bool SetVolume(uint pid, float value)
    {
        try
        {
            var manager = GetSessionManager();
            if (manager == null) return false;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);
            var guid = Guid.Empty;
            bool touched = false;
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var control);
                try
                {
                    if (control is not IAudioSessionControl2 control2) continue;
                    control2.GetProcessId(out uint sessionPid);
                    if (sessionPid != pid) continue;
                    if (control is not ISimpleAudioVolume volume) continue;
                    volume.SetMasterVolume(Math.Clamp(value, 0f, 1f), ref guid);
                    touched = true;
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
            return touched;
        }
        catch
        {
            return false;
        }
    }

    private static IAudioSessionManager2? GetSessionManager()
    {
        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        if (enumeratorType == null) return null;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
        try
        {
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
            try
            {
                var iid = typeof(IAudioSessionManager2).GUID;
                device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
                return (IAudioSessionManager2)obj;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        int GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IAudioSessionControl control);
        int GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out ISimpleAudioVolume volume);
        // IAudioSessionManager2
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification(string sessionId, IntPtr notification);
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid eventContext);
        int GetMute(out bool mute);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        int GetGroupingParam(out Guid grouping);
        int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool enable);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        int GetGroupingParam(out Guid grouping);
        int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
    }
}
