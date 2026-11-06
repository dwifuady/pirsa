using System.Reflection;
using System.Runtime.InteropServices;

namespace Pirsa.Playback.Mpv;

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public int Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNode
{
    // libmpv puts the union first and the format tag after it.
    public MpvNodeUnion U;
    public MpvFormat Format;
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct MpvNodeUnion
{
    [FieldOffset(0)] public IntPtr StringPtr;
    [FieldOffset(0)] public int Flag;
    [FieldOffset(0)] public long Int64;
    [FieldOffset(0)] public double Double;
    [FieldOffset(0)] public IntPtr List;
    [FieldOffset(0)] public IntPtr ByteArray;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNodeList
{
    public int Num;
    public IntPtr Values;
    public IntPtr Keys;
}

/// <summary>
/// Minimal libmpv client API surface used by the WinUI backend. The native
/// library comes from the Endpne.LibMPV.Windows package and lives in a
/// per-architecture folder next to the app, so a resolver points DllImport at
/// it instead of relying on the process search path.
/// </summary>
internal static unsafe class LibMpv
{
    private const string LibraryName = "libmpv-2";

    static LibMpv()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals(LibraryName, StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("libmpv", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in MpvRuntime.LibraryCandidates)
        {
            if (File.Exists(candidate))
            {
                return NativeLibrary.Load(candidate);
            }
        }

        return IntPtr.Zero;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr mpv_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_set_option_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_set_property_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr mpv_get_property_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_set_property(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        void* data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_get_property(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        void* data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_observe_property(
        IntPtr ctx,
        ulong replyUserdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_request_log_messages(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void mpv_free_node_contents(IntPtr node);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_command(IntPtr ctx, IntPtr* args);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int mpv_command_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string command);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr mpv_error_string(int error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern uint mpv_client_api_version();
}

/// <summary>
/// Locates and warms the bundled libmpv build. The NuGet package copies
/// libmpv-2.dll into libmpv/win-x64 or libmpv/win-arm64 in the output.
/// </summary>
internal static class MpvRuntime
{
    private static bool? _available;

    public static IReadOnlyList<string> LibraryCandidates { get; } = BuildCandidates();

    public static bool IsAvailable
    {
        get
        {
            if (_available is { } known)
            {
                return known;
            }

            var exists = LibraryCandidates.Any(File.Exists);
            _available = exists;
            return exists;
        }
    }

    public static Task WarmUpAsync() => Task.Run(() =>
    {
        try
        {
            _ = LibMpv.mpv_client_api_version();
        }
        catch
        {
            _available = false;
        }
    });

    public static string DisplayName => "mpv";

    private static IReadOnlyList<string> BuildCandidates()
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "win-arm64"
            : "win-x64";

        return
        [
            Path.Combine(AppContext.BaseDirectory, "libmpv", architecture, "libmpv-2.dll"),
            Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"),
        ];
    }
}
