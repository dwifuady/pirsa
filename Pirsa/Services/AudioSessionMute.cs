using System.Runtime.InteropServices;

namespace Pirsa.Services;

/// <summary>
/// Mutes this process's own audio session straight through WASAPI.
///
/// libvlc's own mute is not usable for silencing a pause: the audio output
/// thread applies it, and that thread is busy playing the very audio the user
/// wants stopped, so the player log shows libvlc's mute landing 315-618 ms after
/// the request (its unmute lands in 1-2 ms, when that thread is running again).
/// The Windows mixer applies session gain to audio that is still queued in the
/// device ring, so going at the session directly silences a pause immediately.
///
/// Only the default render device is considered: the app plays one stream to one
/// device, and a session that is not there yet simply reports as missing.
/// </summary>
internal static class AudioSessionMute
{
    /// <summary>What <see cref="MuteUnlessAlreadyMuted"/> found and did.</summary>
    public enum Result
    {
        /// <summary>No session of ours on the default device: nothing to mute.</summary>
        NoSession,

        /// <summary>The session was unmuted and this call muted it.</summary>
        Applied,

        /// <summary>Somebody else had already muted it, so it was left alone.</summary>
        AlreadyMuted,

        /// <summary>A session was there but refused the call.</summary>
        Failed,
    }

    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;
    private const int DeviceStateActive = 1;
    private const int ClsCtxAll = 23; // CLSCTX_INPROC_SERVER | LOCAL_SERVER | REMOTE_SERVER

    private static readonly Guid InterfaceIdSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>
    /// Mutes the session unless it is already muted.
    ///
    /// One walk of the session list answers both questions, because the pause
    /// path cannot pay for two: this is called on the UI thread the moment the
    /// user presses pause.
    /// </summary>
    public static Result MuteUnlessAlreadyMuted()
    {
        List<ISimpleAudioVolume> sessions;
        try
        {
            sessions = OwnSessions();
        }
        catch (Exception)
        {
            return Result.Failed;
        }

        if (sessions.Count == 0)
        {
            return Result.NoSession;
        }

        try
        {
            foreach (var session in sessions)
            {
                if (session.GetMute(out var muted) >= 0 && muted)
                {
                    return Result.AlreadyMuted;
                }
            }

            var applied = false;
            foreach (var session in sessions)
            {
                applied |= session.SetMute(true, IntPtr.Zero) >= 0;
            }

            return applied ? Result.Applied : Result.Failed;
        }
        catch (Exception)
        {
            return Result.Failed;
        }
        finally
        {
            ReleaseAll(sessions);
        }
    }

    /// <summary>Mutes or unmutes every audio session this process owns.</summary>
    /// <returns>True when at least one session took the change.</returns>
    public static bool SetMute(bool mute)
    {
        List<ISimpleAudioVolume> sessions;
        try
        {
            sessions = OwnSessions();
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            if (sessions.Count == 0)
            {
                PlayerLog.Write("audio session: none found on active render devices");
                return false;
            }

            var applied = false;
            foreach (var session in sessions)
            {
                applied |= session.SetMute(mute, IntPtr.Zero) >= 0;
            }

            return applied;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ReleaseAll(sessions);
        }
    }

    private static List<ISimpleAudioVolume> OwnSessions()
    {
        var sessions = new List<ISimpleAudioVolume>();
        var processId = (uint)Environment.ProcessId;

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection? collection = null;
        try
        {
            // Enumerate every active render device, not just the default
            // multimedia endpoint: mpv and VLC can pick a different role or
            // device from the one Windows reports as the default.
            if (enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out collection) < 0 ||
                collection.GetCount(out var deviceCount) < 0)
            {
                return sessions;
            }

            for (var deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                if (collection.Item(deviceIndex, out var device) < 0)
                {
                    continue;
                }

                AddDeviceSessions(device, processId, sessions);
                Release(device);
            }

            return sessions;
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
    }

    private static void AddDeviceSessions(
        IMMDevice device,
        uint processId,
        List<ISimpleAudioVolume> sessions)
    {
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? list = null;
        try
        {
            var interfaceId = InterfaceIdSessionManager2;
            if (device.Activate(ref interfaceId, ClsCtxAll, IntPtr.Zero, out manager) < 0 ||
                manager.GetSessionEnumerator(out list) < 0 ||
                list.GetCount(out var count) < 0)
            {
                return;
            }

            for (var index = 0; index < count; index++)
            {
                if (list.GetSession(index, out var session) < 0)
                {
                    continue;
                }

                var ours = session.GetProcessId(out var owner) >= 0 && owner == processId;

                // The session object carries the volume interface as well; the
                // cast is a QueryInterface. Anything not ours is released now
                // rather than at the next collection.
                if (ours && session is ISimpleAudioVolume volume)
                {
                    sessions.Add(volume);
                }
                else
                {
                    Release(session);
                }
            }
        }
        finally
        {
            Release(list);
            Release(manager);
        }
    }

    private static void ReleaseAll(List<ISimpleAudioVolume> sessions)
    {
        foreach (var session in sessions)
        {
            Release(session);
        }

        sessions.Clear();
    }

    private static void Release(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(instance);
        }
        catch (Exception)
        {
            // A session that has already gone is not worth a word.
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    // The interfaces below are declared whole, including the members this file
    // never calls: COM dispatch is by vtable slot, so dropping one would shift
    // every member after it onto the wrong function.
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        int RegisterEndpointNotificationCallback(IntPtr client);

        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);

        int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(
            ref Guid interfaceId,
            int classContext,
            IntPtr activationParams,
            out IAudioSessionManager2 sessionManager);

        int OpenPropertyStore(int access, out IntPtr properties);

        int GetId(out IntPtr id);

        int GetState(out int state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl);

        int GetSimpleAudioVolume(IntPtr audioSessionGuid, int streamFlags, out IntPtr audioVolume);

        // IAudioSessionManager2
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionList);

        int RegisterSessionNotification(IntPtr sessionNotification);

        int UnregisterSessionNotification(IntPtr sessionNotification);

        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);

        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);

        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);

        int GetDisplayName(out IntPtr displayName);

        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);

        int GetIconPath(out IntPtr iconPath);

        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);

        int GetGroupingParam(out Guid groupingId);

        int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);

        int RegisterAudioSessionNotification(IntPtr newNotifications);

        int UnregisterAudioSessionNotification(IntPtr newNotifications);

        // IAudioSessionControl2
        int GetSessionIdentifier(out IntPtr identifier);

        int GetSessionInstanceIdentifier(out IntPtr identifier);

        int GetProcessId(out uint processId);

        int IsSystemSoundsSession();

        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, IntPtr eventContext);

        int GetMasterVolume(out float level);

        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);

        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
