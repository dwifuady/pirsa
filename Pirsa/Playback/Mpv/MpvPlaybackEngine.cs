using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Pirsa.Helpers;
using Pirsa.Services;

namespace Pirsa.Playback.Mpv;

/// <summary>
/// mpv backend using its D3D11 composition output. mpv owns the composition
/// swapchain and publishes its address as <c>display-swapchain</c>; the panel
/// binds that swapchain directly, so the picture is composited by WinUI while
/// the transport chrome stays regular XAML.
/// </summary>
public sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private readonly SwapChainPanel _panel = new()
    {
        IsTabStop = true,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private IntPtr _handle;
    private IntPtr _swapChain;
    private Thread? _eventThread;
    private volatile bool _ready;
    private volatile bool _disposed;
    private volatile bool _fileLoaded;
    private int _state = (int)PlaybackState.Idle;

    public MpvPlaybackEngine()
    {
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("mpv must be created on the UI thread.");

        AutomationProperties.SetName(_panel, "Video");
        PlayerLog.Reset();
        _panel.Loaded += Panel_Loaded;
        _panel.SizeChanged += Panel_SizeChanged;
        _panel.CompositionScaleChanged += Panel_CompositionScaleChanged;
    }

    public string Id => "mpv";

    public FrameworkElement Surface => _panel;

    public bool IsReady => _ready && _handle != IntPtr.Zero;

    public PlaybackState State => (PlaybackState)Volatile.Read(ref _state);

    public bool IsPlaying => State == PlaybackState.Playing;

    public bool IsSeekable => GetFlag("seekable", out var seekable) && seekable;

    public int Volume
    {
        get => GetDouble("volume", out var volume) ? (int)Math.Round(volume) : 0;
        set => SetPropertyString(
            "volume",
            Math.Clamp(value, 0, 100).ToString(CultureInfo.InvariantCulture));
    }

    public bool IsMuted => GetFlag("mute", out var muted) && muted;

    public long PositionMs
    {
        get => GetDouble("time-pos", out var seconds)
            ? (long)Math.Round(seconds * 1000)
            : 0;
        set
        {
            if (value < 0)
            {
                return;
            }

            SetPropertyString(
                "time-pos",
                (value / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    public long DurationMs => GetDouble("duration", out var seconds)
        ? (long)Math.Round(seconds * 1000)
        : 0;

    public IReadOnlyList<PlaybackTrack> AudioTracks
    {
        get
        {
            var tracks = new List<PlaybackTrack> { new(-1, "Off") };

            foreach (var track in ReadTrackList())
            {
                if (!track.Type.Equals("audio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tracks.Add(new PlaybackTrack(
                    track.Id,
                    DisplayName(track),
                    track.Language,
                    track.Title,
                    track.IsExternal));
            }

            return tracks;
        }
    }

    public IReadOnlyList<PlaybackTrack> SubtitleTracks
    {
        get
        {
            var tracks = new List<PlaybackTrack>();

            foreach (var track in ReadTrackList())
            {
                if (!track.Type.Equals("sub", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tracks.Add(new PlaybackTrack(
                    track.Id,
                    DisplayName(track),
                    track.Language,
                    track.Title,
                    track.IsExternal));
            }

            return tracks;
        }
    }

    public long CurrentAudioTrackId
    {
        get
        {
            var aid = GetString("aid");
            if (long.TryParse(aid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }

            // "auto" means mpv picked a track; the track list says which one.
            return ReadTrackList()
                       .FirstOrDefault(track =>
                           track.Selected &&
                           track.Type.Equals("audio", StringComparison.OrdinalIgnoreCase))
                       ?.Id
                   ?? -1;
        }
    }

    public event EventHandler? Ready;

    public event EventHandler? Playing;

    public event EventHandler? Paused;

    public event EventHandler? Stopped;

    public event EventHandler? EndReached;

    public event EventHandler<PlaybackErrorEventArgs>? Failed;

    public event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;

    public event EventHandler<PlaybackDurationChangedEventArgs>? DurationChanged;

    public event EventHandler<PlaybackBufferingEventArgs>? BufferingChanged;

    public event EventHandler<PlaybackTracksChangedEventArgs>? TracksChanged;

    public bool TryPlay(PlaybackMedia media)
    {
        if (!IsReady)
        {
            EnsureInitialized();
            if (!IsReady)
            {
                return false;
            }
        }

        SetPropertyString(
            "start",
            media.StartPositionMs > 0
                ? $"+{(media.StartPositionMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)}"
                : "none");

        SetPropertyString("http-header-fields", BuildHeaderFields(media.Headers));

        var cacheSeconds = media.IsLive ? "2.5" : "3";
        SetPropertyString("cache-secs", cacheSeconds);
        SetPropertyString("demuxer-readahead-secs", cacheSeconds);

        _fileLoaded = false;
        SetState(PlaybackState.Opening);
        return Command("loadfile", media.Url, "replace") >= 0;
    }

    public void SetPaused(bool paused)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        SetPropertyString("pause", paused ? "yes" : "no");
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        Command("stop");
        _fileLoaded = false;
        SetState(PlaybackState.Stopped);
    }

    public void SetRate(double rate)
    {
        if (rate <= 0)
        {
            return;
        }

        SetPropertyString("speed", rate.ToString("0.###", CultureInfo.InvariantCulture));
    }

    public void SetMuted(bool muted) => SetPropertyString("mute", muted ? "yes" : "no");

    public bool SelectAudioTrack(long id) =>
        SetPropertyString("aid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));

    public bool SelectSubtitleTrack(long id) =>
        SetPropertyString("sid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));

    public bool AddSubtitleFile(string path) =>
        _handle != IntPtr.Zero &&
        !string.IsNullOrWhiteSpace(path) &&
        Command("sub-add", path, "select") >= 0;

    public void SetSubtitleDelay(long delayMs) =>
        SetPropertyString(
            "sub-delay",
            (delayMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));

    public void SetSubtitleLift(int devicePixels)
    {
        // mpv's sub-margin-y is in scaled pixels at a 720p window height.
        var panelHeightPixels = _panel.ActualHeight * _panel.CompositionScaleY;
        var scaled = panelHeightPixels > 0
            ? devicePixels * 720.0 / panelHeightPixels
            : devicePixels;

        SetPropertyString(
            "sub-margin-y",
            Math.Max(0, (int)Math.Round(scaled)).ToString(CultureInfo.InvariantCulture));
    }

    public bool TryGetVideoSize(out uint width, out uint height)
    {
        width = 0;
        height = 0;

        if (!GetInt64("width", out var w) ||
            !GetInt64("height", out var h) ||
            w <= 0 ||
            h <= 0)
        {
            return false;
        }

        width = (uint)w;
        height = (uint)h;
        return true;
    }

    public void FocusSurface(FocusState state = FocusState.Programmatic) =>
        _panel.Focus(state);

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ready = false;
        DetachSurface();

        var handle = _handle;
        await Task.Run(() => TearDown(handle));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ready = false;
        DetachSurface();
        TearDown(_handle);
    }

    // ---------------------------------------------------------------- lifecycle

    private void EnsureInitialized()
    {
        if (_handle != IntPtr.Zero || _disposed)
        {
            return;
        }

        var (width, height) = PixelSize();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            var handle = LibMpv.mpv_create();
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("mpv_create failed.");
            }

            _handle = handle;

            // Options below must be set before mpv_initialize.
            Option("config", "no");
            Option("terminal", "no");
            Option("msg-level", "all=info");
            Option("vo", "gpu-next");
            Option("gpu-context", "d3d11");
            Option("d3d11-output-mode", "composition");
            Option("d3d11-composition-size", $"{width}x{height}");
            Option("force-window", "immediate");
            Option("hwdec", "auto-safe");
            Option("idle", "yes");
            Option("keep-open", "yes");
            Option("osc", "no");
            Option("osd-level", "0");
            Option("input-default-bindings", "no");
            Option("input-vo-keyboard", "no");
            Option("audio-client-name", "Pirsa");
            Option("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Pirsa/1.0");
            Option("sub-auto", "no");
            Option("cache", "yes");
            Option("cache-secs", "3");
            Option("demuxer-readahead-secs", "3");
            Option("volume", "80");

            _ = LibMpv.mpv_request_log_messages(_handle, "info");

            var initialized = LibMpv.mpv_initialize(_handle);
            if (initialized < 0)
            {
                throw new InvalidOperationException(
                    $"mpv_initialize failed: {ErrorString(initialized)}");
            }

            Observe("display-swapchain", MpvFormat.Int64);
            Observe("time-pos", MpvFormat.Double);
            Observe("duration", MpvFormat.Double);
            Observe("pause", MpvFormat.Flag);
            Observe("seekable", MpvFormat.Flag);
            Observe("seeking", MpvFormat.Flag);
            Observe("track-list", MpvFormat.Node);
            Observe("cache-buffering-state", MpvFormat.Double);
            Observe("paused-for-cache", MpvFormat.Flag);
            Observe("width", MpvFormat.Int64);
            Observe("height", MpvFormat.Int64);
            Observe("aid", MpvFormat.String);
            Observe("sid", MpvFormat.String);

            _eventThread = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "mpv events",
            };
            _eventThread.Start();

            _ready = true;
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            if (_handle != IntPtr.Zero)
            {
                try
                {
                    LibMpv.mpv_terminate_destroy(_handle);
                }
                catch
                {
                }

                _handle = IntPtr.Zero;
            }

            Failed?.Invoke(
                this,
                new PlaybackErrorEventArgs(
                    $"The mpv engine couldn't start. {exception.Message}",
                    isFatal: true));
        }
    }

    private void TearDown(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            _handle = IntPtr.Zero;
            return;
        }

        try
        {
            _ = LibMpv.mpv_command_string(handle, "quit");
        }
        catch
        {
        }

        if (_eventThread is { } thread && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }

        try
        {
            LibMpv.mpv_terminate_destroy(handle);
        }
        catch
        {
        }

        _eventThread = null;
        _handle = IntPtr.Zero;
    }

    private void DetachSurface()
    {
        _swapChain = IntPtr.Zero;

        try
        {
            SwapChainPanelInterop.SetSwapChain(_panel, IntPtr.Zero);
        }
        catch
        {
            // The panel may already be gone during shutdown.
        }
    }

    // ---------------------------------------------------------------- panel glue

    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        EnsureInitialized();
    }

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_handle == IntPtr.Zero)
        {
            EnsureInitialized();
            return;
        }

        UpdateOutputSize();
    }

    private void Panel_CompositionScaleChanged(SwapChainPanel sender, object args)
    {
        if (_disposed || _handle == IntPtr.Zero)
        {
            return;
        }

        UpdateOutputSize();
    }

    private (int Width, int Height) PixelSize()
    {
        var scaleX = _panel.CompositionScaleX > 0 ? _panel.CompositionScaleX : 1.0f;
        var scaleY = _panel.CompositionScaleY > 0 ? _panel.CompositionScaleY : 1.0f;

        return (
            (int)Math.Round(_panel.ActualWidth * scaleX),
            (int)Math.Round(_panel.ActualHeight * scaleY));
    }

    private void UpdateOutputSize()
    {
        var (width, height) = PixelSize();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        SetPropertyString("d3d11-composition-size", $"{width}x{height}");
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        if (_swapChain == IntPtr.Zero)
        {
            return;
        }

        var scaleX = _panel.CompositionScaleX > 0 ? _panel.CompositionScaleX : 1.0f;
        var scaleY = _panel.CompositionScaleY > 0 ? _panel.CompositionScaleY : 1.0f;

        if (!SwapChainPanelInterop.SetTransform(_swapChain, 1.0f / scaleX, 1.0f / scaleY))
        {
            PlayerLog.Write("mpv swapchain transform failed");
        }
    }

    private void OnSwapChainChanged(IntPtr swapChain)
    {
        if (_disposed || swapChain == _swapChain)
        {
            return;
        }

        _swapChain = swapChain;

        try
        {
            SwapChainPanelInterop.SetSwapChain(_panel, swapChain);
            PlayerLog.Write(
                $"mpv bound swapchain 0x{swapChain.ToInt64():x} " +
                $"panel={_panel.ActualWidth:0}x{_panel.ActualHeight:0} " +
                $"scale={_panel.CompositionScaleX:0.##}x{_panel.CompositionScaleY:0.##}");
        }
        catch (Exception exception)
        {
            // The panel may be tearing down.
            PlayerLog.Write($"mpv swapchain bind failed: {exception.Message}");
            return;
        }

        ApplyTransform();

        if (swapChain != IntPtr.Zero)
        {
            UpdateOutputSize();
        }
    }

    // ---------------------------------------------------------------- event loop

    private void EventLoop()
    {
        while (true)
        {
            var eventPointer = LibMpv.mpv_wait_event(_handle, -1);
            if (eventPointer == IntPtr.Zero)
            {
                continue;
            }

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);

            switch (mpvEvent.EventId)
            {
                case MpvEventId.Shutdown:
                    return;

                case MpvEventId.LogMessage:
                    HandleLog(mpvEvent.Data);
                    break;

                case MpvEventId.PropertyChange:
                    HandlePropertyChange(mpvEvent.Data);
                    break;

                case MpvEventId.StartFile:
                    SetState(PlaybackState.Opening);
                    break;

                case MpvEventId.FileLoaded:
                    _fileLoaded = true;
                    Post(() =>
                    {
                        TracksChanged?.Invoke(this, new PlaybackTracksChangedEventArgs(PlaybackTrackKind.Audio));
                        TracksChanged?.Invoke(this, new PlaybackTracksChangedEventArgs(PlaybackTrackKind.Subtitle));
                    });
                    break;

                case MpvEventId.PlaybackRestart:
                    if (_fileLoaded &&
                        State != PlaybackState.Playing &&
                        !(GetFlag("pause", out var paused) && paused))
                    {
                        SetState(PlaybackState.Playing);
                        Post(() => Playing?.Invoke(this, EventArgs.Empty));
                    }
                    break;

                case MpvEventId.VideoReconfig:
                    Post(() =>
                    {
                        TracksChanged?.Invoke(this, new PlaybackTracksChangedEventArgs(PlaybackTrackKind.Audio));
                        TracksChanged?.Invoke(this, new PlaybackTracksChangedEventArgs(PlaybackTrackKind.Subtitle));
                    });
                    break;

                case MpvEventId.EndFile:
                    HandleEndFile(mpvEvent.Data);
                    break;

                case MpvEventId.QueueOverflow:
                    PlayerLog.Write("mpv event queue overflow");
                    break;
            }
        }
    }

    private void HandleLog(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        var message = Marshal.PtrToStructure<MpvEventLogMessage>(data);
        var prefix = Marshal.PtrToStringUTF8(message.Prefix) ?? "";
        var level = Marshal.PtrToStringUTF8(message.Level) ?? "";
        var text = (Marshal.PtrToStringUTF8(message.Text) ?? "").TrimEnd();

        var normalized = level switch
        {
            "fatal" or "error" => "Error",
            "warn" => "Warning",
            "debug" or "v" or "trace" => "Debug",
            _ => "Notice",
        };

        PlayerLog.WriteEngine("mpv", normalized, prefix, text);
    }

    private void HandlePropertyChange(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        var property = Marshal.PtrToStructure<MpvEventProperty>(data);
        var name = Marshal.PtrToStringUTF8(property.Name) ?? "";
        var hasData = property.Data != IntPtr.Zero;

        switch (name)
        {
            case "display-swapchain":
            {
                if (!hasData || property.Format != MpvFormat.Int64)
                {
                    break;
                }

                var value = Marshal.ReadInt64(property.Data);
                PlayerLog.Write($"mpv display-swapchain=0x{value:x}");
                Post(() => OnSwapChainChanged((IntPtr)value));
                break;
            }

            case "time-pos":
            {
                if (!hasData || property.Format != MpvFormat.Double)
                {
                    break;
                }

                var seconds = Marshal.PtrToStructure<double>(property.Data);
                var position = (long)Math.Round(seconds * 1000);
                Post(() => PositionChanged?.Invoke(this, new PlaybackPositionChangedEventArgs(position)));
                break;
            }

            case "duration":
            {
                if (!hasData || property.Format != MpvFormat.Double)
                {
                    break;
                }

                var seconds = Marshal.PtrToStructure<double>(property.Data);
                var duration = (long)Math.Round(seconds * 1000);
                Post(() => DurationChanged?.Invoke(this, new PlaybackDurationChangedEventArgs(duration)));
                break;
            }

            case "pause":
            {
                if (!hasData || property.Format != MpvFormat.Flag)
                {
                    break;
                }

                var isPaused = Marshal.ReadInt32(property.Data) != 0;

                if (isPaused)
                {
                    if (State != PlaybackState.Paused)
                    {
                        SetState(PlaybackState.Paused);
                        Post(() => Paused?.Invoke(this, EventArgs.Empty));
                    }
                }
                else if (_fileLoaded && State != PlaybackState.Playing)
                {
                    // PlaybackRestart is not guaranteed after every resume, and
                    // the pause flag is the authoritative state transition.
                    SetState(PlaybackState.Playing);
                    Post(() => Playing?.Invoke(this, EventArgs.Empty));
                }

                break;
            }

            case "cache-buffering-state":
            {
                if (!hasData || property.Format != MpvFormat.Double)
                {
                    break;
                }

                var cache = (float)Marshal.PtrToStructure<double>(property.Data);
                Post(() => BufferingChanged?.Invoke(this, new PlaybackBufferingEventArgs(cache)));
                break;
            }

            case "track-list":
            {
                Post(() =>
                {
                    TracksChanged?.Invoke(this, new PlaybackTracksChangedEventArgs(PlaybackTrackKind.Audio));
                    TracksChanged?.Invoke(this, new PlaybackTracksChangedEventArgs(PlaybackTrackKind.Subtitle));
                });
                break;
            }

            case "aid":
                Post(() => TracksChanged?.Invoke(
                    this,
                    new PlaybackTracksChangedEventArgs(PlaybackTrackKind.Audio)));
                break;

            case "sid":
                Post(() => TracksChanged?.Invoke(
                    this,
                    new PlaybackTracksChangedEventArgs(PlaybackTrackKind.Subtitle)));
                break;
        }
    }

    private void HandleEndFile(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        var end = Marshal.PtrToStructure<MpvEventEndFile>(data);
        var reason = (MpvEndFileReason)end.Reason;
        _fileLoaded = false;

        switch (reason)
        {
            case MpvEndFileReason.Eof:
                SetState(PlaybackState.Ended);
                Post(() => EndReached?.Invoke(this, EventArgs.Empty));
                break;

            case MpvEndFileReason.Error:
                SetState(PlaybackState.Error);
                var message = $"The selected source stopped responding or uses a format mpv can't open. ({ErrorString(end.Error)})";
                Post(() => Failed?.Invoke(this, new PlaybackErrorEventArgs(message)));
                break;

            default:
                SetState(PlaybackState.Stopped);
                if (!_disposed)
                {
                    Post(() => Stopped?.Invoke(this, EventArgs.Empty));
                }

                break;
        }
    }

    private void SetState(PlaybackState state) => Volatile.Write(ref _state, (int)state);

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
    }

    // ---------------------------------------------------------------- libmpv glue

    private void Option(string name, string value)
    {
        if (_handle != IntPtr.Zero)
        {
            _ = LibMpv.mpv_set_option_string(_handle, name, value);
        }
    }

    private void Observe(string name, MpvFormat format)
    {
        if (_handle != IntPtr.Zero)
        {
            _ = LibMpv.mpv_observe_property(_handle, 0, name, format);
        }
    }

    private bool SetPropertyString(string name, string value) =>
        _handle != IntPtr.Zero &&
        LibMpv.mpv_set_property_string(_handle, name, value) >= 0;

    private string? GetString(string name)
    {
        if (_handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = LibMpv.mpv_get_property_string(_handle, name);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            LibMpv.mpv_free(pointer);
        }
    }

    private unsafe bool GetDouble(string name, out double value)
    {
        value = 0;
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        var result = 0.0;
        if (LibMpv.mpv_get_property(_handle, name, MpvFormat.Double, &result) < 0)
        {
            return false;
        }

        value = result;
        return true;
    }

    private unsafe bool GetFlag(string name, out bool value)
    {
        value = false;
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        var result = 0;
        if (LibMpv.mpv_get_property(_handle, name, MpvFormat.Flag, &result) < 0)
        {
            return false;
        }

        value = result != 0;
        return true;
    }

    private unsafe bool GetInt64(string name, out long value)
    {
        value = 0;
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        var result = 0L;
        if (LibMpv.mpv_get_property(_handle, name, MpvFormat.Int64, &result) < 0)
        {
            return false;
        }

        value = result;
        return true;
    }

    private unsafe int Command(params string[] arguments)
    {
        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        var pointers = new IntPtr[arguments.Length + 1];

        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                pointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            }

            fixed (IntPtr* pointer = pointers)
            {
                return LibMpv.mpv_command(_handle, pointer);
            }
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    private static string BuildHeaderFields(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return "";
        }

        return string.Join(
            ",",
            headers.Select(header => $"{header.Key}: {header.Value}"));
    }

    // ---------------------------------------------------------------- tracks

    private unsafe IReadOnlyList<TrackInfo> ReadTrackList()
    {
        if (_handle == IntPtr.Zero)
        {
            return [];
        }

        var nodeSize = Marshal.SizeOf<MpvNode>();
        var memory = Marshal.AllocHGlobal(nodeSize);

        try
        {
            if (LibMpv.mpv_get_property(_handle, "track-list", MpvFormat.Node, (void*)memory) < 0)
            {
                return [];
            }

            var node = Marshal.PtrToStructure<MpvNode>(memory);
            if (node.Format != MpvFormat.NodeArray || node.U.List == IntPtr.Zero)
            {
                return [];
            }

            var list = Marshal.PtrToStructure<MpvNodeList>(node.U.List);
            var tracks = new List<TrackInfo>(Math.Max(0, list.Num));

            for (var index = 0; index < list.Num; index++)
            {
                var item = Marshal.PtrToStructure<MpvNode>(
                    IntPtr.Add(list.Values, index * nodeSize));

                if (item.Format != MpvFormat.NodeMap)
                {
                    continue;
                }

                if (MapInt64(item, "id") is not { } id ||
                    MapString(item, "type") is not { } type)
                {
                    continue;
                }

                tracks.Add(new TrackInfo(
                    id,
                    type,
                    MapString(item, "lang"),
                    MapString(item, "title"),
                    MapFlag(item, "external"),
                    MapFlag(item, "selected")));
            }

            return tracks;
        }
        catch
        {
            return [];
        }
        finally
        {
            LibMpv.mpv_free_node_contents(memory);
            Marshal.FreeHGlobal(memory);
        }
    }

    private static string DisplayName(TrackInfo track)
    {
        var language = string.IsNullOrWhiteSpace(track.Language)
            ? null
            : SubtitleLanguages.DisplayName(track.Language);

        if (!string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(language))
        {
            return $"{language} · {track.Title}";
        }

        if (!string.IsNullOrWhiteSpace(track.Title))
        {
            return track.Title!;
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            return language!;
        }

        if (!string.IsNullOrWhiteSpace(track.Language))
        {
            return track.Language!;
        }

        return $"Track {track.Id}";
    }

    private static bool TryGetMapValue(MpvNode map, string key, out MpvNode value)
    {
        value = default;

        if (map.Format != MpvFormat.NodeMap || map.U.List == IntPtr.Zero)
        {
            return false;
        }

        var list = Marshal.PtrToStructure<MpvNodeList>(map.U.List);
        if (list.Keys == IntPtr.Zero || list.Values == IntPtr.Zero)
        {
            return false;
        }

        var nodeSize = Marshal.SizeOf<MpvNode>();

        for (var index = 0; index < list.Num; index++)
        {
            var keyPointer = Marshal.ReadIntPtr(list.Keys, index * IntPtr.Size);
            var name = keyPointer == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUTF8(keyPointer);

            if (!string.Equals(name, key, StringComparison.Ordinal))
            {
                continue;
            }

            value = Marshal.PtrToStructure<MpvNode>(
                IntPtr.Add(list.Values, index * nodeSize));
            return true;
        }

        return false;
    }

    private static string? MapString(MpvNode map, string key) =>
        TryGetMapValue(map, key, out var value) && value.Format == MpvFormat.String
            ? Marshal.PtrToStringUTF8(value.U.StringPtr)
            : null;

    private static long? MapInt64(MpvNode map, string key)
    {
        if (!TryGetMapValue(map, key, out var value))
        {
            return null;
        }

        return value.Format switch
        {
            MpvFormat.Int64 => value.U.Int64,
            MpvFormat.Double => (long)value.U.Double,
            _ => null,
        };
    }

    private static bool MapFlag(MpvNode map, string key) =>
        TryGetMapValue(map, key, out var value) &&
        value.Format == MpvFormat.Flag &&
        value.U.Flag != 0;

    private static string ErrorString(int error)
    {
        var pointer = LibMpv.mpv_error_string(error);
        return pointer == IntPtr.Zero
            ? $"error {error}"
            : Marshal.PtrToStringUTF8(pointer) ?? $"error {error}";
    }

    private sealed record TrackInfo(
        long Id,
        string Type,
        string? Language,
        string? Title,
        bool IsExternal,
        bool Selected);
}
