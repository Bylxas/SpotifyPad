using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SpotifyPad;

/// <summary>Sends playback commands straight to the Spotify window and changes Spotify's mixer volume.</summary>
static class SpotifyControl
{
    const int WM_APPCOMMAND = 0x0319;
    const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;

    const byte VK_VOLUME_MUTE = 0xAD;
    const byte VK_VOLUME_DOWN = 0xAE;
    const byte VK_VOLUME_UP = 0xAF;
    const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    const byte VK_MEDIA_PREV_TRACK = 0xB1;
    const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    static IntPtr cachedWindow;

    /// <param name="amount">Volume up/down: percent to change (0 = configured step). Set volume: target percent.</param>
    public static void Execute(PadAction action, AppConfig cfg, int amount = 0)
    {
        int volumeDelta = amount == 0 ? cfg.VolumeStep : amount;
        switch (action)
        {
            case PadAction.PlayPause: Media(APPCOMMAND_MEDIA_PLAY_PAUSE, VK_MEDIA_PLAY_PAUSE, cfg); break;
            case PadAction.Next: Media(APPCOMMAND_MEDIA_NEXTTRACK, VK_MEDIA_NEXT_TRACK, cfg); break;
            case PadAction.Previous: Media(APPCOMMAND_MEDIA_PREVIOUSTRACK, VK_MEDIA_PREV_TRACK, cfg); break;
            case PadAction.VolumeUp: Volume(+volumeDelta, VK_VOLUME_UP, cfg); break;
            case PadAction.VolumeDown: Volume(-volumeDelta, VK_VOLUME_DOWN, cfg); break;
            case PadAction.Mute: Volume(0, VK_VOLUME_MUTE, cfg); break;
            case PadAction.SetVolume: SetVolume(amount, cfg.VolumeTarget); break;
        }
    }

    static void SetVolume(int percent, VolumeTarget target)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (target == VolumeTarget.System)
        {
            AppAudio.SetSystemVolume(percent);
            return;
        }
        var pids = SpotifyPids();
        if (target == VolumeTarget.Spotify && pids.Count > 0 && SpotifyDevTools.TrySetVolume(percent)) return;
        MixerVolume(pids, (sessions, ctx) =>
        {
            foreach (var s in sessions)
            {
                s.SetMasterVolume(percent / 100f, ref ctx);
                if (percent > 0) s.SetMute(0, ref ctx);
            }
        });
    }

    /// <summary>The current volume in percent of what <paramref name="target"/> controls, or null if unknown.</summary>
    public static int? GetVolume(VolumeTarget target)
    {
        switch (target)
        {
            case VolumeTarget.System:
                return AppAudio.GetSystemVolume();
            case VolumeTarget.Spotify:
                // Only the slider itself: the mixer value would be misleading here
                return SpotifyPids().Count > 0 ? SpotifyDevTools.GetVolume() : null;
            default:
                int? result = null;
                MixerVolume(SpotifyPids(), (sessions, _) =>
                {
                    sessions[0].GetMasterVolume(out float level);
                    sessions[0].GetMute(out int muted);
                    result = muted != 0 ? 0 : (int)MathF.Round(level * 100);
                });
                return result;
        }
    }

    /// <summary>Runs <paramref name="use"/> on Spotify's mixer sessions (if any) and releases them.</summary>
    static void MixerVolume(HashSet<uint> pids, Action<List<ISimpleAudioVolume>, Guid> use)
    {
        var sessions = AppAudio.GetSessions(pids);
        try
        {
            if (sessions.Count > 0) use(sessions, Guid.Empty);
        }
        finally
        {
            foreach (var s in sessions) Marshal.ReleaseComObject(s);
        }
    }

    static void Media(int appCommand, byte vk, AppConfig cfg)
    {
        if (cfg.SpotifyDirect)
        {
            var hwnd = FindSpotifyWindow(SpotifyPids());
            if (hwnd != IntPtr.Zero)
            {
                PostMessage(hwnd, WM_APPCOMMAND, hwnd, (IntPtr)(appCommand << 16));
                return;
            }
        }
        // Spotify window not found (or direct mode off): global media key
        Key(vk);
    }

    /// <param name="delta">Percent to change, 0 = toggle mute.</param>
    static void Volume(int delta, byte vk, AppConfig cfg)
    {
        if (cfg.VolumeTarget == VolumeTarget.System)
        {
            Key(vk);
            return;
        }

        var pids = SpotifyPids();
        if (cfg.VolumeTarget == VolumeTarget.Spotify)
        {
            if (pids.Count > 0 && SpotifyDevTools.TryAdjustVolume(delta)) return;
            // Spotify not started with the DevTools flag: fall back to its mixer entry (also no focus change)
        }

        var sessions = AppAudio.GetSessions(pids);
        try
        {
            if (sessions.Count == 0) return;
            var ctx = Guid.Empty;
            if (delta == 0)
            {
                sessions[0].GetMute(out int muted);
                foreach (var s in sessions) s.SetMute(muted == 0 ? 1 : 0, ref ctx);
            }
            else
            {
                sessions[0].GetMasterVolume(out float level);
                float next = Math.Clamp(MathF.Round(level * 100 + delta) / 100f, 0f, 1f);
                foreach (var s in sessions)
                {
                    s.SetMasterVolume(next, ref ctx);
                    if (delta > 0) s.SetMute(0, ref ctx);
                }
            }
        }
        finally
        {
            foreach (var s in sessions) Marshal.ReleaseComObject(s);
        }
    }

    /// <summary>
    /// True while a song plays: Spotify titles its window "Artist - Song" then, and "Spotify…" when paused or idle.
    /// </summary>
    public static bool IsPlaying()
    {
        var hwnd = FindSpotifyWindow(SpotifyPids());
        if (hwnd == IntPtr.Zero) return false;
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        return title.Length > 0 && !title.ToString().StartsWith("Spotify", StringComparison.Ordinal);
    }

    static HashSet<uint> SpotifyPids()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("Spotify"))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        return pids;
    }

    static IntPtr FindSpotifyWindow(HashSet<uint> pids)
    {
        if (pids.Count == 0) return IntPtr.Zero;
        if (cachedWindow != IntPtr.Zero && IsSpotifyMainWindow(cachedWindow, pids)) return cachedWindow;

        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsSpotifyMainWindow(h, pids)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        cachedWindow = found;
        return found;
    }

    static bool IsSpotifyMainWindow(IntPtr h, HashSet<uint> pids)
    {
        if (!IsWindow(h)) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (!pids.Contains(pid) || GetWindowTextLength(h) == 0) return false;
        var cls = new StringBuilder(64);
        GetClassName(h, cls, cls.Capacity);
        return cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);
    }

    static void Key(byte vk)
    {
        const int KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, IntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int flags, IntPtr extra);
}

/// <summary>Minimal Windows Core Audio interop to find per-app volume sessions.</summary>
static class AppAudio
{
    /// <summary>Returns the volume controls of every audio session owned by one of the given processes.</summary>
    public static List<ISimpleAudioVolume> GetSessions(HashSet<uint> pids)
    {
        var result = new List<ISimpleAudioVolume>();
        if (pids.Count == 0) return result;

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            const int eRender = 0, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23;
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices) != 0) return result;
            devices.GetCount(out int deviceCount);
            for (int d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0) continue;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object obj) == 0)
                {
                    var manager = (IAudioSessionManager2)obj;
                    if (manager.GetSessionEnumerator(out var sessions) == 0)
                    {
                        sessions.GetCount(out int count);
                        for (int i = 0; i < count; i++)
                        {
                            if (sessions.GetSession(i, out var control) != 0) continue;
                            if (control.GetProcessId(out uint pid) == 0 && pids.Contains(pid))
                                result.Add((ISimpleAudioVolume)control); // same COM object, released by caller
                            else
                                Marshal.ReleaseComObject(control);
                        }
                        Marshal.ReleaseComObject(sessions);
                    }
                    Marshal.ReleaseComObject(manager);
                }
                Marshal.ReleaseComObject(device);
            }
            Marshal.ReleaseComObject(devices);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    /// <summary>Windows master volume of the default output device in percent, or null.</summary>
    public static int? GetSystemVolume()
    {
        int? result = null;
        WithEndpointVolume(v =>
        {
            v.GetMasterVolumeLevelScalar(out float level);
            result = (int)MathF.Round(level * 100);
        });
        return result;
    }

    public static void SetSystemVolume(int percent)
    {
        WithEndpointVolume(v =>
        {
            var ctx = Guid.Empty;
            v.SetMasterVolumeLevelScalar(percent / 100f, ref ctx);
        });
    }

    static void WithEndpointVolume(Action<IAudioEndpointVolume> use)
    {
        const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0) return;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object obj) == 0)
            {
                use((IAudioEndpointVolume)obj);
                Marshal.ReleaseComObject(obj);
            }
            Marshal.ReleaseComObject(device);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        // Unused methods are declared to keep the vtable order
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl (unused, but needed for vtable order)
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid context);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute(int mute, ref Guid context);
    [PreserveSig] int GetMute(out int mute);
}
