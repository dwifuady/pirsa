using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Pirsa.Services;

namespace Pirsa.Playback.LibVlc;

/// <summary>
/// Lifts libVLC's own subtitles clear of the playback chrome.
///
/// VLC draws subtitles into the video output and its only handle on their
/// height is the vout's "sub-margin" variable: a bottom margin in display
/// pixels. That variable has a live callback that repositions the current
/// subtitles without restarting the stream, but libvlc 3 exposes no setter for
/// it. This bridges to it with exported functions only: the vout is fetched
/// through the input API, then the margin is written with the variable API.
///
/// Everything here is best effort. If a future libvlc changes these internals
/// the calls fail, the player logs it once, and subtitles simply stay put.
/// </summary>
internal static class SubtitleLift
{
    /// <summary>input_query_e.INPUT_GET_VOUTS in libvlc 3.0.x.</summary>
    private const int InputGetVouts = 43;

    /// <summary>VLC_VAR_INTEGER from vlc_variables.h.</summary>
    private const int VarInteger = 0x0030;

    private static readonly object Gate = new();
    private static bool _broken;

    /// <summary>
    /// Sets the margin, in device pixels, between the bottom of the picture and
    /// the bottom of the subtitles. Zero restores VLC's default position.
    /// </summary>
    public static void Set(MediaPlayer? player, int devicePixels) =>
        ApplyToVouts(
            player,
            vout => SetInteger(vout, "sub-margin", Math.Max(0, devicePixels)));

    /// <summary>
    /// Updates the live text renderer. The freetype filter inherits these
    /// variables from the vout, so they apply without restarting playback.
    /// </summary>
    public static void SetStyle(
        MediaPlayer? player,
        int textScalePercent,
        int backgroundOpacity)
    {
        ApplyToVouts(
            player,
            vout =>
            {
                SetInteger(vout, "sub-text-scale", Math.Clamp(textScalePercent, 50, 200));
                SetInteger(vout, "freetype-background-opacity", Math.Clamp(backgroundOpacity, 0, 255));
                SetInteger(vout, "freetype-background-color", 0x000000);
            });
    }

    private static void ApplyToVouts(MediaPlayer? player, Action<IntPtr> apply)
    {
        if (player is null)
        {
            return;
        }

        lock (Gate)
        {
            if (_broken)
            {
                return;
            }

            try
            {
                ApplyCore(player.NativeReference, apply);
            }
            catch (Exception exception)
                when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                // Subtitle styling is a nicety; a libvlc that no longer
                // exports these entry points must not break playback.
                _broken = true;
                PlayerLog.Write($"subtitle styling unavailable: {exception.Message}");
            }
            catch (Exception exception)
            {
                PlayerLog.Write($"subtitle styling failed: {exception.Message}");
            }
        }
    }

    private static void ApplyCore(IntPtr mediaPlayer, Action<IntPtr> apply)
    {
        var input = GetInputThread(mediaPlayer);
        if (input == IntPtr.Zero)
        {
            return;
        }

        var countSlot = Marshal.AllocHGlobal(IntPtr.Size);
        var listSlot = Marshal.AllocHGlobal(IntPtr.Size);
        var list = IntPtr.Zero;

        try
        {
            // input_Control is variadic: the two pointers are the out
            // parameters of INPUT_GET_VOUTS (vout_thread_t ***, size_t *).
            Marshal.WriteIntPtr(countSlot, IntPtr.Zero);
            Marshal.WriteIntPtr(listSlot, IntPtr.Zero);
            if (InputControl(input, InputGetVouts, listSlot, countSlot) != 0)
            {
                return;
            }

            list = Marshal.ReadIntPtr(listSlot);
            var count = (int)Marshal.ReadIntPtr(countSlot).ToInt64();
            for (var index = 0; index < count; index++)
            {
                var vout = Marshal.ReadIntPtr(list, index * IntPtr.Size);
                if (vout == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    apply(vout);
                }
                finally
                {
                    // Each pointer in the list is a held reference.
                    ObjectRelease(vout);
                }
            }
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                FreeVoutList(list);
            }

            Marshal.FreeHGlobal(listSlot);
            Marshal.FreeHGlobal(countSlot);
            ObjectRelease(input);
        }
    }

    private static void SetInteger(IntPtr target, string name, int value) =>
        VarSetChecked(target, name, VarInteger, new VlcValue { Integer = value });

    /// <summary>
    /// libvlccore allocates the vout list with its own C runtime, so it must be
    /// released there too: msvcrt on x86/x64, the UCRT on ARM64. Both are the
    /// shared system runtimes the bundled libvlc 3.0.24 binaries import.
    /// </summary>
    private static void FreeVoutList(IntPtr list)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            UcrtFree(list);
        }
        else
        {
            MsvcrtFree(list);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct VlcValue
    {
        [FieldOffset(0)]
        public long Integer;
    }

    [DllImport("libvlc", EntryPoint = "libvlc_get_input_thread", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetInputThread(IntPtr mediaPlayer);

    [DllImport("libvlccore", EntryPoint = "input_Control", CallingConvention = CallingConvention.Cdecl)]
    private static extern int InputControl(IntPtr input, int query, IntPtr first, IntPtr second);

    [DllImport("libvlccore", EntryPoint = "var_SetChecked", CallingConvention = CallingConvention.Cdecl)]
    private static extern int VarSetChecked(
        IntPtr target,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int type,
        VlcValue value);

    [DllImport("libvlccore", EntryPoint = "vlc_object_release", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ObjectRelease(IntPtr target);

    [DllImport("msvcrt", EntryPoint = "free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MsvcrtFree(IntPtr block);

    [DllImport("ucrtbase", EntryPoint = "free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void UcrtFree(IntPtr block);
}
