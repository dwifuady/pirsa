using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pirsa.Services.Sync.Providers;

/// <summary>
/// Stores the sync document in one file inside a user-supplied WebDAV folder.
/// No third-party account is involved, which makes it the fallback for anyone
/// who does not want to connect Trakt, Simkl or MDBList.
/// </summary>
public sealed class WebDavSyncProvider : ISyncProvider
{
    internal const string DocumentName = "pirsa-sync.json";

    public SyncProviderDescriptor Descriptor { get; } = new(
        Id: "webdav",
        Name: "WebDAV",
        Description: "Nextcloud, ownCloud, Box or any other WebDAV folder you control.",
        Coverage: "Movies · Series",
        Capabilities: SyncCapabilities.Watched | SyncCapabilities.Watchlist,
        AuthKind: SyncAuthKind.UrlWithPassword,
        ConnectLabel: "Connect",
        Placeholder: "https://cloud.example.com/remote.php/dav/files/me/pirsa",
        Hint: "Pirsa writes one file, pirsa-sync.json, in this folder. Use an app password rather than your account password.");

    public async Task<ISyncSession> ConnectAsync(
        SyncCredentials credentials,
        IProgress<SyncAuthPrompt>? prompt = null,
        CancellationToken cancellationToken = default)
    {
        var raw = credentials.Url?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new StremioException("Enter the WebDAV folder URL.");
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new StremioException("Enter a full http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(credentials.UserName))
        {
            throw new StremioException("Enter the WebDAV user name.");
        }

        var session = new WebDavSyncSession(
            uri,
            credentials.UserName.Trim(),
            credentials.Password ?? "");

        await session.ValidateAsync(cancellationToken);
        return session;
    }
}

/// <summary>
/// One connected WebDAV folder. The document is read and written by URL; the
/// folder itself is never enumerated.
/// </summary>
internal sealed class WebDavSyncSession : ISyncSession
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;
    private readonly Uri _folderUri;
    private readonly Uri _fileUri;
    private readonly string _userName;
    private readonly string _password;

    public WebDavSyncSession(Uri target, string userName, string password)
    {
        _userName = userName;
        _password = password;

        // Accept either the folder itself or the document URL; the form asks
        // for a folder, but a pasted file URL should still work.
        var path = target.AbsoluteUri;
        if (path.EndsWith(WebDavSyncProvider.DocumentName, StringComparison.OrdinalIgnoreCase))
        {
            _fileUri = target;
            _folderUri = new Uri(path[..^(WebDavSyncProvider.DocumentName.Length)]);
        }
        else
        {
            if (!path.EndsWith('/'))
            {
                path += "/";
            }

            _folderUri = new Uri(path);
            _fileUri = new Uri(_folderUri, WebDavSyncProvider.DocumentName);
        }

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        AccountLabel = $"{userName} · {target.Host}";
    }

    public string AccountLabel { get; }

    /// <summary>WebDAV has no server-side cursor; the whole document is the state.</summary>
    public string? Cursor { get; set; }

    public SyncCredentials? UpdatedCredentials => null;

    /// <summary>
    /// Proves the URL and credentials work before the connection is saved.
    /// A 404 on the document is expected on first use; a 401/403 is not.
    /// </summary>
    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var propfind = await SendAsync(
            new HttpMethod("PROPFIND"),
            _folderUri,
            content: null,
            depth: "0",
            cancellationToken);

        if (propfind.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new StremioException("The server rejected those credentials.");
        }

        if (propfind.Status is HttpStatusCode.NotFound)
        {
            throw new StremioException("That WebDAV folder does not exist.");
        }

        // Some servers refuse PROPFIND (or want a body for it). Falling back
        // to the document still proves the credentials: an auth failure is
        // answered before a missing file.
        if (!IsSuccess(propfind.Status))
        {
            var probe = await SendAsync(
                HttpMethod.Get,
                _fileUri,
                content: null,
                depth: null,
                cancellationToken);

            if (probe.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new StremioException("The server rejected those credentials.");
            }
        }
    }

    public async Task<RemoteSnapshot> PullAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            _fileUri,
            content: null,
            depth: null,
            cancellationToken);

        if (response.Status is HttpStatusCode.NotFound)
        {
            return RemoteSnapshot.Empty;
        }

        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new StremioException("The server rejected those credentials.");
        }

        if (!IsSuccess(response.Status))
        {
            throw new StremioException($"The server answered {(int)response.Status}.");
        }

        var document = JsonSerializer.Deserialize<WebDavDocument>(response.Body, JsonOptions)
            ?? new WebDavDocument();

        return new RemoteSnapshot(
            [.. (document.Watched ?? []).Where(IsValid)],
            [.. (document.Watchlist ?? []).Where(IsValid)]);
    }

    public async Task PushAsync(
        RemoteSnapshot remote,
        SyncSnapshot local,
        CancellationToken cancellationToken = default)
    {
        // Watched and unwatched facts both travel, so another device can tell
        // "never seen" apart from "removed here".
        var document = new WebDavDocument
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Watched = [.. local.Watched, .. local.Unwatched],
            Watchlist = [.. local.Watchlist, .. local.WatchlistRemoved],
        };

        var json = JsonSerializer.Serialize(document, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await SendAsync(
            HttpMethod.Put,
            _fileUri,
            content,
            depth: null,
            cancellationToken);

        if (!IsSuccess(response.Status))
        {
            throw new StremioException($"Saving to WebDAV failed ({(int)response.Status}).");
        }
    }

    /// <summary>WebDAV has no scrobble endpoint; resume push is a later slice.</summary>
    public Task ScrobbleAsync(
        PlaybackSignal signal,
        PlaybackSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemovePlaybackAsync(string key, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<WebDavResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        string? depth,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);

        if (!string.IsNullOrEmpty(_userName))
        {
            var raw = $"{_userName}:{_password}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        if (depth is not null)
        {
            request.Headers.Add("Depth", depth);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (content is not null)
        {
            request.Content = content;
        }

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new WebDavResponse(response.StatusCode, body);
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static bool IsValid(WatchEvent item) =>
        item is { ImdbId.Length: > 2 } &&
        item.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
        item.At != default;

    private static bool IsValid(WatchlistChange item) =>
        item is { ImdbId.Length: > 2 } &&
        item.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
        item.At != default;

    private sealed record WebDavResponse(HttpStatusCode Status, string Body);

    /// <summary>
    /// The on-disk shape. Positional records serialize their constructor
    /// parameters, so the wire model and the document stay one definition.
    /// </summary>
    private sealed class WebDavDocument
    {
        public int Version { get; set; } = 1;

        public DateTimeOffset UpdatedAt { get; set; }

        public List<WatchEvent>? Watched { get; set; } = [];

        public List<WatchlistChange>? Watchlist { get; set; } = [];
    }
}
