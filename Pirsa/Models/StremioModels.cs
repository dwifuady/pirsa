using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Pirsa.Models;

/// <summary>
/// A Stremio addon manifest (https://stremio.github.io/stremio-addon-sdk/).
/// Only the fields Pirsa needs to discover and browse catalogs are modelled.
/// </summary>
public sealed class AddonManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("types")]
    public List<string> Types { get; set; } = [];

    [JsonPropertyName("idPrefixes")]
    public List<string> IdPrefixes { get; set; } = [];

    [JsonPropertyName("resources")]
    [JsonConverter(typeof(AddonResourceListJsonConverter))]
    public List<AddonResource> Resources { get; set; } = [];

    [JsonPropertyName("catalogs")]
    public List<AddonCatalog> Catalogs { get; set; } = [];

    [JsonIgnore]
    public bool SupportsCatalog => SupportsResource("catalog");

    public bool SupportsStream(string type, string id) => SupportsResource("stream", type, id);

    public bool SupportsSubtitles(string type, string id) => SupportsResource("subtitles", type, id);

    /// <summary>
    /// Resources may be declared as plain strings or as objects that scope the
    /// resource to certain media types and id prefixes (PenguPlay does the latter).
    /// </summary>
    public bool SupportsResource(string name, string? type = null, string? id = null)
    {
        if (Resources.Count == 0)
        {
            return name.Equals("catalog", StringComparison.OrdinalIgnoreCase);
        }

        var resource = Resources.FirstOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            return false;
        }

        if (type is not null && resource.Types.Count > 0 &&
            !resource.Types.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (id is not null)
        {
            var prefixes = resource.IdPrefixes.Count > 0 ? resource.IdPrefixes : IdPrefixes;
            if (prefixes.Count > 0 &&
                !prefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One entry in an addon manifest's <c>resources</c> array.
/// </summary>
public sealed class AddonResource
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("types")]
    public List<string> Types { get; set; } = [];

    [JsonPropertyName("idPrefixes")]
    public List<string> IdPrefixes { get; set; } = [];
}

/// <summary>
/// Accepts both the legacy string form ("stream") and the object form
/// ({ name, types, idPrefixes }) used by newer addons.
/// </summary>
internal sealed class AddonResourceListJsonConverter : JsonConverter<List<AddonResource>>
{
    public override List<AddonResource> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a resources array.");
        }

        var resources = new List<AddonResource>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return resources;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                resources.Add(new AddonResource { Name = reader.GetString() ?? "" });
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                var resource = JsonSerializer.Deserialize<AddonResource>(ref reader, options);
                if (resource is not null)
                {
                    resources.Add(resource);
                }
            }
            else
            {
                reader.Skip();
            }
        }

        throw new JsonException("Unterminated resources array.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<AddonResource> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var resource in value)
        {
            writer.WriteStartObject();
            writer.WriteString("name", resource.Name);
            if (resource.Types.Count > 0)
            {
                writer.WritePropertyName("types");
                JsonSerializer.Serialize(writer, resource.Types, options);
            }
            if (resource.IdPrefixes.Count > 0)
            {
                writer.WritePropertyName("idPrefixes");
                JsonSerializer.Serialize(writer, resource.IdPrefixes, options);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// A catalog declaration inside an addon manifest.
/// </summary>
public sealed class AddonCatalog
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("extra")]
    public List<CatalogExtra> Extra { get; set; } = [];

    [JsonPropertyName("extraRequired")]
    public List<string> ExtraRequired { get; set; } = [];

    [JsonIgnore]
    public CatalogExtra? RequiredGenre =>
        Extra.FirstOrDefault(e => e.IsRequired && e.IsGenre && e.Options.Count > 0);
}

/// <summary>
/// An extra parameter a catalog supports (genre, skip, search, year, ...).
/// </summary>
public sealed class CatalogExtra
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; }

    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];

    [JsonIgnore]
    public bool IsGenre => Name.Equals("genre", StringComparison.OrdinalIgnoreCase);
}

internal sealed class CatalogResponse
{
    [JsonPropertyName("metas")]
    public List<MetaItem> Metas { get; set; } = [];
}

internal sealed class MetaResponse
{
    [JsonPropertyName("meta")]
    public MetaItem? Meta { get; set; }
}

internal sealed class StreamResponse
{
    [JsonPropertyName("streams")]
    public List<StreamItem> Streams { get; set; } = [];
}

/// <summary>
/// One playable source returned by an addon's stream resource.
/// </summary>
public sealed class StreamItem
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("externalUrl")]
    public string? ExternalUrl { get; set; }

    [JsonPropertyName("infoHash")]
    public string? InfoHash { get; set; }

    [JsonPropertyName("fileIdx")]
    public int? FileIndex { get; set; }

    [JsonPropertyName("behaviorHints")]
    public StreamBehaviorHints? BehaviorHints { get; set; }

    [JsonIgnore]
    public bool HasDirectUrl =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [JsonIgnore]
    public bool IsTorrent => !string.IsNullOrWhiteSpace(InfoHash);

    [JsonIgnore]
    public string? RequestReferer =>
        BehaviorHints?.ProxyHeaders?.Request is { } headers &&
        headers.TryGetValue("Referer", out var referer) ? referer : null;

    [JsonIgnore]
    public string? RequestUserAgent =>
        BehaviorHints?.ProxyHeaders?.Request is { } headers &&
        headers.TryGetValue("User-Agent", out var ua) ? ua : null;

    [JsonIgnore]
    public string? RequestCookie =>
        BehaviorHints?.ProxyHeaders?.Request is { } headers &&
        headers.TryGetValue("Cookie", out var cookie) ? cookie : null;
}

public sealed class StreamBehaviorHints
{
    [JsonPropertyName("notWebReady")]
    public bool NotWebReady { get; set; }

    [JsonPropertyName("bingeGroup")]
    public string? BingeGroup { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("videoSize")]
    public long? VideoSize { get; set; }

    [JsonPropertyName("proxyHeaders")]
    public StreamProxyHeaders? ProxyHeaders { get; set; }
}

public sealed class StreamProxyHeaders
{
    [JsonPropertyName("request")]
    public Dictionary<string, string>? Request { get; set; }
}

internal sealed class SubtitleResponse
{
    [JsonPropertyName("subtitles")]
    public List<SubtitleItem> Subtitles { get; set; } = [];
}

/// <summary>One subtitle track advertised by an addon.</summary>
public sealed class SubtitleItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("lang")]
    public string? Lang { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("subtitleFileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("movieReleaseName")]
    public string? ReleaseName { get; set; }

    [JsonPropertyName("releaseGroup")]
    public string? ReleaseGroup { get; set; }

    [JsonPropertyName("fpsMilli")]
    public int? FpsMilli { get; set; }

    [JsonPropertyName("SubEncoding")]
    public string? Encoding { get; set; }

    [JsonIgnore]
    public bool HasUrl =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// A Stremio meta preview (catalog item) or full meta object (details).
/// Cinemeta returns the same shape for both, so one model covers both uses.
/// </summary>
public sealed class MetaItem : ObservableObject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Manifest URL of the addon that returned this item.</summary>
    [JsonIgnore]
    public string? SourceAddonUrl { get; set; }

    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("releaseInfo")]
    public string? ReleaseInfo { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    [JsonPropertyName("imdbRating")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? ImdbRating { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("awards")]
    public string? Awards { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("released")]
    public string? Released { get; set; }

    [JsonPropertyName("genres")]
    public List<string>? Genres { get; set; }

    [JsonPropertyName("genre")]
    public List<string>? Genre { get; set; }

    [JsonPropertyName("cast")]
    public List<string>? Cast { get; set; }

    [JsonPropertyName("director")]
    public List<string>? Director { get; set; }

    [JsonPropertyName("writer")]
    public List<string>? Writer { get; set; }

    [JsonPropertyName("videos")]
    public List<MetaVideo>? Videos { get; set; }

    private BitmapImage? _posterCard;
    private BitmapImage? _posterDetail;
    private BitmapImage? _background;
    private BitmapImage? _logo;

    [JsonIgnore]
    public IReadOnlyList<string> GenreList =>
        Genres is { Count: > 0 } ? Genres : Genre ?? [];

    [JsonIgnore]
    public string GenreLine => string.Join(", ", GenreList.Take(2));

    [JsonIgnore]
    public string YearText =>
        !string.IsNullOrWhiteSpace(ReleaseInfo) ? ReleaseInfo : (Year ?? "");

    [JsonIgnore]
    public string RatingText =>
        decimal.TryParse(ImdbRating, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var rating)
            ? rating.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            : "";

    [JsonIgnore]
    public bool HasRating => !string.IsNullOrWhiteSpace(RatingText);

    [JsonIgnore]
    public string HeroMetaLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(YearText)) parts.Add(YearText);
            if (!string.IsNullOrWhiteSpace(Runtime)) parts.Add(Runtime);
            if (!string.IsNullOrWhiteSpace(GenreLine)) parts.Add(GenreLine);
            return string.Join("  ·  ", parts);
        }
    }

    [JsonIgnore]
    public string CardMeta
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(YearText)) parts.Add(YearText);
            if (HasRating) parts.Add($"★ {RatingText}");
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore]
    public string TypeLabel => Type.ToLowerInvariant() switch
    {
        "movie" => "Movie",
        "series" => "Series",
        "tv" => "Live TV",
        "channel" => "Live TV",
        "music" => "Music",
        "podcast" => "Podcast",
        _ => string.IsNullOrWhiteSpace(Type) ? "Title" : Type,
    };

    [JsonIgnore]
    public bool HasPoster => !string.IsNullOrWhiteSpace(Poster);

    [JsonIgnore]
    public bool HasNoPoster => !HasPoster;

    [JsonIgnore]
    public BitmapImage? PosterCardImage =>
        _posterCard ??= ImageLoader.Load(ImageLoader.UpgradePoster(Poster), 360);

    [JsonIgnore]
    public BitmapImage? PosterDetailImage =>
        _posterDetail ??= ImageLoader.Load(ImageLoader.UpgradePoster(Poster), 640);

    [JsonIgnore]
    public BitmapImage? BackgroundImage =>
        _background ??= ImageLoader.Load(Background, 1400);

    [JsonIgnore]
    public BitmapImage? LogoImage =>
        _logo ??= ImageLoader.Load(Logo, 640);

    [JsonIgnore]
    public bool HasLogo => LogoImage is not null;

    public override string ToString() =>
        string.IsNullOrWhiteSpace(YearText) ? Name : $"{Name} ({YearText})";
}

/// <summary>
/// A single episode (or video) attached to a series meta object.
/// </summary>
public sealed class MetaVideo : ObservableObject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("season")]
    public int Season { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("episode")]
    public int Episode { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("released")]
    public string? Released { get; set; }

    [JsonPropertyName("firstAired")]
    public string? FirstAired { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? Rating { get; set; }

    private BitmapImage? _thumbnail;

    [JsonIgnore]
    public int EpisodeNumber => Number > 0 ? Number : Math.Max(Episode, 0);

    [JsonIgnore]
    public string EpisodeTag => EpisodeNumber > 0 ? $"E{EpisodeNumber:00}" : "S";

    [JsonIgnore]
    public bool HasNumber => EpisodeNumber > 0;

    [JsonIgnore]
    public string Title =>
        string.IsNullOrWhiteSpace(Name) ? $"Episode {EpisodeNumber}" : Name!;

    [JsonIgnore]
    public string AccessibleTitle =>
        EpisodeNumber > 0 ? $"Episode {EpisodeNumber}, {Title}" : Title;

    [JsonIgnore]
    public string DescriptionText =>
        !string.IsNullOrWhiteSpace(Overview) ? Overview! :
        !string.IsNullOrWhiteSpace(Description) ? Description! : "";

    [JsonIgnore]
    public string AirDateText
    {
        get
        {
            var raw = !string.IsNullOrWhiteSpace(AiredDate) ? AiredDate : null;
            return DateTimeOffset.TryParse(raw, out var date)
                ? date.ToString("MMM d, yyyy")
                : "";
        }
    }

    [JsonIgnore]
    public string AiredDate => FirstAired ?? Released ?? "";

    [JsonIgnore]
    public BitmapImage? ThumbnailImage =>
        _thumbnail ??= ImageLoader.Load(Thumbnail, 360);
}

/// <summary>
/// Builds WinUI image sources from remote poster/backdrop URLs.
/// </summary>
internal static class ImageLoader
{
    public static BitmapImage? Load(string? url, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return new BitmapImage
        {
            UriSource = uri,
            DecodePixelWidth = decodeWidth,
        };
    }

    /// <summary>
    /// Cinemeta serves low-resolution posters at /poster/small/. Ask for the
    /// medium rendition for card and detail surfaces when available.
    /// </summary>
    public static string? UpgradePoster(string? url) =>
        url?.Replace("/poster/small/", "/poster/medium/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Addons in the wild return some scalar fields as either strings or numbers
/// (Cinemeta episode ratings are numeric). Read both without failing the page.
/// </summary>
internal sealed class FlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var integer)
                ? integer.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected {reader.TokenType} for a text value."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
