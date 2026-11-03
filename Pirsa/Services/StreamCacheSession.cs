using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pirsa.Services;

/// <summary>
/// A local, disk-backed read-ahead cache in front of VLC.
///
/// VLC answers every seek with a brand new HTTP request to the provider, so a
/// source that is only slightly faster than real time makes every skip wait for
/// the network. Playing <c>http://127.0.0.1:port/…</c> instead moves that request
/// to a file on disk: bytes already downloaded are served instantly, backward
/// skips included, and everything else is fetched once, in the background, ahead
/// of the playhead.
///
/// Two download slots run in parallel because a single connection cannot keep
/// up with what the player asks for: MP4 files with the index at the end make
/// VLC read the tail before it reads the start, and VLC keeps a second
/// connection open for its own probing.
///
/// The session is deliberately conservative: it only engages for a plain
/// on-demand HTTP file that advertises byte ranges and a length. Live streams,
/// HLS, and servers that ignore ranges fall back to the direct URL, because
/// caching them adds delay without ever making a seek instant.
/// </summary>
public sealed class StreamCacheSession : IAsyncDisposable
{
    /// <summary>How far ahead of the furthest read each download slot may run.</summary>
    private const long PrefetchLimitBytes = 128L * 1024 * 1024;

    /// <summary>
    /// A read this close ahead of a slot is a reader catching up with its own
    /// download, so it waits. Anything further ahead is a seek or an MP4 index
    /// at the end of the file, and gets a downloader redirected to it instead
    /// of waiting for the sequential one to arrive.
    /// </summary>
    private const long JumpToleranceBytes = 128 * 1024;

    /// <summary>Size of the probe request, and therefore the bytes it throws away.</summary>
    private const long ProbeBytes = 64 * 1024;

    private const int ChunkBytes = 128 * 1024;

    /// <summary>Cached files older than this are left over from a killed session.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object CleanupGate = new();
    private static bool _cleaned;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _dataArrived = new(0);
    private readonly List<(long Start, long End)> _complete = [];
    private readonly Slot[] _slots = [new(), new()];
    private readonly HashSet<long> _waiters = [];
    private long _readerSequence;
    private readonly TcpListener _listener;
    private readonly Uri _upstream;
    private readonly Dictionary<string, string> _headers;
    private readonly SafeFileHandle _readHandle;
    private readonly SafeFileHandle _writeHandle;
    private readonly string _path;
    private readonly string _contentType;
    private readonly long _length;
    private readonly Task[] _downloaders = new Task[2];

    private StreamCacheSession(
        Uri upstream,
        IReadOnlyDictionary<string, string>? headers,
        long length,
        string contentType,
        string path)
    {
        _upstream = upstream;
        _headers = NormalizeHeaders(headers);
        _length = length;
        _contentType = contentType;
        _path = path;

        _writeHandle = File.OpenHandle(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        _readHandle = File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/stream{ExtensionFor(upstream)}";
    }

    /// <summary>The localhost URL to hand to VLC. It needs no provider headers.</summary>
    public string Url { get; }

    public long Length => _length;

    public string Path => _path;

    /// <summary>
    /// Probes the source and starts a cached session, or returns null when this
    /// source should play directly. Never throws: a cache is a nicety, playback
    /// is not.
    /// </summary>
    public static async Task<StreamCacheSession?> TryCreateAsync(
        Uri source,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        if (source.Scheme is not ("http" or "https") || IsPlaylist(source))
        {
            return null;
        }

        CleanupStaleFiles();

        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, source);
            foreach (var header in NormalizeHeaders(headers))
            {
                probe.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            probe.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);

            using var response = await Http.SendAsync(
                probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // No 206 means the provider ignores ranges, so the cache could never
            // serve anything but playback straight through. Leave it alone.
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                return null;
            }

            var length = response.Content.Headers.ContentRange?.Length ?? -1;
            if (length <= 0)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var session = new StreamCacheSession(
                source, headers, length, contentType, CreateCacheFile(source));

            session.Start();
            PlayerLog.Write($"cache on for {source} ({length} bytes, {contentType}) -> {session.Url}");
            return session;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PlayerLog.Write($"cache unavailable, playing direct: {exception.Message}");
            return null;
        }
    }

    private void Start()
    {
        var token = _cts.Token;
        for (var i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            _downloaders[i] = Task.Run(() => RunSlotAsync(slot, token));
        }

        _ = AcceptLoopAsync(token);
    }

    // ------------------------------------------------------------ downloaders

    private async Task RunSlotAsync(Slot slot, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await slot.Work.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            long start;
            CancellationTokenSource regionCts;
            lock (_gate)
            {
                if (slot.Pending is not { } pending)
                {
                    continue;
                }

                start = pending;
                slot.Pending = null;
                slot.Start = start;
                slot.End = start;
                slot.Demand = start;
                slot.Active = true;
                regionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                slot.Cts = regionCts;
            }

            try
            {
                await DownloadRegionAsync(slot, start, regionCts.Token);
            }
            catch (OperationCanceledException)
            {
                // A read wanted somewhere else; what arrived is still usable.
            }
            catch (Exception exception)
            {
                PlayerLog.Write($"cache slot failed at {start}: {exception.Message}");
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (slot.End > slot.Start)
                    {
                        _complete.Add((slot.Start, slot.End));
                    }

                    slot.Active = false;
                    slot.End = slot.Start;
                    slot.Cts?.Dispose();
                    slot.Cts = null;
                }

                ReleaseData();
            }
        }
    }

    private async Task DownloadRegionAsync(Slot slot, long start, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _upstream);
        foreach (var header in _headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        PlayerLog.Write($"cache fetch from {start}");
        request.Headers.Range = new RangeHeaderValue(start, null);

        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
        {
            throw new HttpRequestException($"upstream answered {(int)response.StatusCode}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);

        try
        {
            var position = start;
            while (true)
            {
                var read = await body.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    return;
                }

                var capped = (int)Math.Min(read, _length - position);
                if (capped <= 0)
                {
                    return;
                }

                RandomAccess.Write(_writeHandle, buffer.AsSpan(0, capped), position);
                position += capped;

                long demand;
                lock (_gate)
                {
                    slot.End = position;
                    demand = slot.Demand;
                    if (slot.Pending is not null)
                    {
                        // A read wants somewhere else: stop early, keep what we have.
                        return;
                    }
                }

                ReleaseData();

                while (position - demand > PrefetchLimitBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(250, cancellationToken);
                    lock (_gate)
                    {
                        demand = slot.Demand;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ----------------------------------------------------------------- server

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, cancellationToken));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, cancellationToken);
            if (request is null)
            {
                return;
            }

            var (isHead, from, to, hasRange) = request.Value;
            from = Math.Clamp(from, 0, _length - 1);
            to = Math.Clamp(to, from, _length - 1);

            var readerId = Interlocked.Increment(ref _readerSequence);
            PlayerLog.Write($"cache read {from}-{to} (reader {readerId})");

            var head = new StringBuilder();
            head.Append(hasRange
                ? "HTTP/1.1 206 Partial Content\r\n"
                : "HTTP/1.1 200 OK\r\n");
            head.Append($"Content-Type: {_contentType}\r\n");
            head.Append("Accept-Ranges: bytes\r\n");
            if (hasRange)
            {
                head.Append($"Content-Range: bytes {from}-{to}/{_length}\r\n");
            }

            head.Append($"Content-Length: {to - from + 1}\r\n");
            head.Append("Connection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken);

            if (isHead)
            {
                return;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                var offset = from;
                while (offset <= to)
                {
                    var available = await WaitForDataAsync(offset, readerId, cancellationToken);
                    if (available <= offset)
                    {
                        return;
                    }

                    var size = (int)Math.Min(buffer.Length, Math.Min(to - offset + 1, available - offset));
                    var read = RandomAccess.Read(_readHandle, buffer.AsSpan(0, size), offset);
                    if (read <= 0)
                    {
                        return;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    offset += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or IOException or SocketException)
        {
            // VLC closing a connection on seek is routine.
        }
    }

    private readonly record struct UpstreamRequest(bool IsHead, long From, long To, bool HasRange);

    private async Task<UpstreamRequest?> ReadRequestAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new StringBuilder();
        var buffer = new byte[1];
        while (header.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            header.Append((char)buffer[0]);
            if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n")
            {
                break;
            }
        }

        var lines = header.ToString().Split("\r\n");
        if (lines.Length == 0)
        {
            return null;
        }

        var isHead = lines[0].StartsWith("HEAD", StringComparison.OrdinalIgnoreCase);

        long from = 0;
        long to = _length - 1;
        var range = lines.FirstOrDefault(line =>
            line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));

        if (range is not null)
        {            var spec = range["Range:".Length..].Trim();
            var equals = spec.IndexOf('=');
            if (equals >= 0)
            {
                spec = spec[(equals + 1)..];
            }

            var parts = spec.Split('-');
            if (long.TryParse(parts[0], out var start))
            {
                from = start;
                if (parts.Length > 1 && long.TryParse(parts[1], out var end))
                {
                    to = end;
                }
            }
        }

        return new UpstreamRequest(isHead, from, to, range is not null);
    }

    /// <summary>
    /// Returns how many bytes from <paramref name="offset"/> are on disk, waiting
    /// for a download slot and steering it when the data is somewhere else.
    ///
    /// Only the newest connection that is waiting gets to steer a downloader.
    /// VLC opens a fresh connection per seek but can leave the previous one open,
    /// and those stale readers want the old position: if they were allowed to
    /// steer, two readers in different places would keep sending the same two
    /// downloaders back and forth and neither would ever get its data. The
    /// newest connection is the user's current intent, so it wins.
    /// </summary>
    private async Task<long> WaitForDataAsync(
        long offset, long readerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _waiters.Add(readerId);
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                CancellationTokenSource? superseded = null;
                long available;
                lock (_gate)
                {
                    foreach (var slot in _slots)
                    {
                        slot.Demand = Math.Max(slot.Demand, offset);
                    }

                    if (TryGetAvailable(offset, out available))
                    {
                        return available;
                    }

                    // Already heading there, or close enough that waiting wins.
                    var coming = _slots.Any(slot =>
                        slot.Pending == offset ||
                        (slot.Active && offset >= slot.Start && offset - slot.End <= JumpToleranceBytes));

                    if (!coming && IsNewestWaiter(readerId))
                    {
                        var target = PickSlotForJump(offset);
                        target.Pending = offset;
                        if (target.Work.CurrentCount == 0)
                        {
                            target.Work.Release();
                        }

                        superseded = target.Cts;
                        PlayerLog.Write($"cache steer {offset} to slot {Array.IndexOf(_slots, target)}");
                    }
                }

                // Cancelling under the lock can run a downloader continuation inline.
                superseded?.Cancel();
                await _dataArrived.WaitAsync(250, cancellationToken);
            }
        }
        finally
        {
            lock (_gate)
            {
                _waiters.Remove(readerId);
            }
        }
    }

    private bool IsNewestWaiter(long readerId) =>
        _waiters.Count == 0 || readerId == _waiters.Max();

    private bool TryGetAvailable(long offset, out long availableEnd)
    {
        foreach (var slot in _slots)
        {
            if (slot.Active && offset >= slot.Start && offset < slot.End)
            {
                availableEnd = slot.End;
                return true;
            }
        }

        foreach (var (start, end) in _complete)
        {
            if (offset >= start && offset < end)
            {
                availableEnd = end;
                return true;
            }
        }

        availableEnd = -1;
        return false;
    }

    /// <summary>Picks the cheapest slot to send somewhere else: idle, else least filled.</summary>
    private Slot PickSlotForJump(long offset)
    {
        Slot? best = null;
        foreach (var slot in _slots)
        {
            if (!slot.Active && slot.Pending is null)
            {
                return slot;
            }

            if (best is null || slot.End - slot.Start < best.End - best.Start)
            {
                best = slot;
            }
        }

        return best ?? _slots[0];
    }

    private void ReleaseData()
    {
        if (_dataArrived.CurrentCount == 0)
        {
            _dataArrived.Release();
        }
    }

    // -------------------------------------------------------------- lifecycle

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            await Task.WhenAll(_downloaders);
        }
        catch
        {
        }

        _readHandle.Dispose();
        _writeHandle.Dispose();
        foreach (var slot in _slots)
        {
            slot.Work.Dispose();
        }

        _cts.Dispose();
        _dataArrived.Dispose();

        // VLC may still hold the file open for a moment after Stop().
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(_path);
                return;
            }
            catch
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private sealed class Slot
    {
        public readonly SemaphoreSlim Work = new(0);
        public long Start;
        public long End;
        public long Demand;
        public long? Pending;
        public bool Active;
        public CancellationTokenSource? Cts;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static Dictionary<string, string> NormalizeHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result;
        }

        foreach (var (key, value) in headers)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static bool IsPlaylist(Uri source)
    {
        var path = source.AbsolutePath;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtensionFor(Uri source)
    {
        var extension = System.IO.Path.GetExtension(source.AbsolutePath);
        return extension.Length is > 1 and < 8 ? extension : ".bin";
    }

    private static string CreateCacheFile(Uri source)
    {
        var directory = Directory.CreateDirectory(LocalPaths.StreamCache);

        return System.IO.Path.Combine(
            directory.FullName, $"{Guid.NewGuid():N}{ExtensionFor(source)}");
    }

    /// <summary>Removes files a crashed or killed session left behind.</summary>
    private static void CleanupStaleFiles()
    {
        lock (CleanupGate)
        {
            if (_cleaned)
            {
                return;
            }

            _cleaned = true;
            try
            {
                var directory = new DirectoryInfo(LocalPaths.StreamCache);

                if (!directory.Exists)
                {
                    return;
                }

                foreach (var file in directory.GetFiles())
                {
                    if (DateTime.UtcNow - file.LastWriteTimeUtc > StaleAfter)
                    {
                        file.Delete();
                    }
                }
            }
            catch
            {
            }
        }
    }
}
