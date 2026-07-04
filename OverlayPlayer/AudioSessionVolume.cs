using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoSyncPrototype.OverlayPlayer;

/// <summary>
/// Sets the Windows per-application volume (the level you'd drag in the Volume Mixer) for
/// this process and all of its child processes.
///
/// This exists because WebView2 plays a YouTube Watch2Gether room inside a cross-origin
/// iframe living in a separate <c>msedgewebview2.exe</c> process, which the page's own
/// JavaScript can't reach. Setting <c>CoreWebView2.IsMuted</c> handles mute, but there is no
/// WebView2 API for a volume LEVEL — so the only reliable knob is the OS audio session. We
/// walk our process tree and set the master volume/mute on every audio session that belongs
/// to one of those processes. Everything is best-effort and swallowed on failure: if the
/// interop can't find the session, the audio simply stays at the browser's default (i.e. no
/// worse than before).
/// </summary>
internal static class AudioSessionVolume
{
    public static void Apply(float volume, float pan, bool muted)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        pan = Math.Clamp(pan, -1f, 1f);

        // Constant-ish spatial pan by trimming one stereo channel: pan right -> quiet the
        // left channel and vice-versa. Crude next to a WebAudio panner, but it's the only
        // way to steer a room's audio (whose real panner is locked inside the iframe).
        var leftChannel = pan > 0f ? Math.Max(0f, 1f - pan) : 1f;
        var rightChannel = pan < 0f ? Math.Max(0f, 1f + pan) : 1f;

        var targetPids = GetProcessTreePids();

        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
            try
            {
                var managerIid = typeof(IAudioSessionManager2).GUID;
                device.Activate(ref managerIid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var managerObj);
                var manager = (IAudioSessionManager2)managerObj;
                try
                {
                    manager.GetSessionEnumerator(out var sessions);
                    sessions.GetCount(out var count);
                    for (var i = 0; i < count; i++)
                    {
                        sessions.GetSession(i, out var control);
                        try
                        {
                            if (control is IAudioSessionControl2 control2)
                            {
                                control2.GetProcessId(out var pid);
                                if (targetPids.Contains(pid))
                                {
                                    var context = Guid.Empty;
                                    if (control is ISimpleAudioVolume simpleVolume)
                                    {
                                        simpleVolume.SetMasterVolume(volume, ref context);
                                        simpleVolume.SetMute(muted, ref context);
                                    }

                                    // Stereo pan (only meaningful for 2-channel sessions).
                                    if (control is IChannelAudioVolume channelVolume)
                                    {
                                        channelVolume.GetChannelCount(out var channelCount);
                                        if (channelCount == 2)
                                        {
                                            channelVolume.SetChannelVolume(0, leftChannel, ref context);
                                            channelVolume.SetChannelVolume(1, rightChannel, ref context);
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(control);
                        }
                    }

                    Marshal.ReleaseComObject(sessions);
                }
                finally
                {
                    Marshal.ReleaseComObject(manager);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch
        {
            // Best effort only — leave the browser's default volume in place.
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static HashSet<uint> GetProcessTreePids()
    {
        var self = (uint)Environment.ProcessId;
        var result = new HashSet<uint> { self };

        var childrenByParent = new Dictionary<uint, List<uint>>();
        foreach (var (pid, parentPid) in EnumerateProcesses())
        {
            if (!childrenByParent.TryGetValue(parentPid, out var list))
            {
                list = [];
                childrenByParent[parentPid] = list;
            }

            list.Add(pid);
        }

        var queue = new Queue<uint>();
        queue.Enqueue(self);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!childrenByParent.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (result.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        foreach (var process in Process.GetProcessesByName("msedgewebview2"))
        {
            try
            {
                result.Add((uint)process.Id);
            }
            catch
            {
                // Process exited while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private static IEnumerable<(uint Pid, uint ParentPid)> EnumerateProcesses()
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    yield return (entry.th32ProcessID, entry.th32ParentProcessID);
                }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    // Only the methods we call carry real signatures; every earlier vtable slot is declared
    // as a no-arg stub purely to preserve the slot ordering (they are never invoked).
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints();

        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(
            ref Guid iid,
            uint clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl();

        void GetSimpleAudioVolume();

        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int sessionCount);

        void GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        void GetState();

        void GetDisplayName();

        void SetDisplayName();

        void GetIconPath();

        void SetIconPath();

        void GetGroupingParam();

        void SetGroupingParam();

        void RegisterAudioSessionNotification();

        void UnregisterAudioSessionNotification();

        void GetSessionIdentifier();

        void GetSessionInstanceIdentifier();

        void GetProcessId(out uint processId);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid eventContext);

        void GetMasterVolume(out float level);

        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport]
    [Guid("1C158861-B533-4B30-B1CF-E853E51C59B8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChannelAudioVolume
    {
        void GetChannelCount(out uint channelCount);

        void SetChannelVolume(uint index, float level, ref Guid eventContext);

        void GetChannelVolume(uint index, out float level);
    }
}
