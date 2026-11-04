using System.Diagnostics.CodeAnalysis;

namespace Pirsa.Services;

/// <summary>
/// Reads the addon links Stremio shares and turns them into a manifest URL.
///
/// <para>
/// An install link is the manifest URL with its scheme removed and
/// <c>stremio://</c> in its place — <c>stremio://example.com/manifest.json</c>
/// is <c>https://example.com/manifest.json</c>. Some sites percent-encode the
/// whole URL, so <c>stremio://https%3A%2F%2Fexample.com%2Fmanifest.json</c> is
/// accepted too. The web share form
/// (<c>https://web.stremio.com/#/addons?addon=…</c>) and a plain manifest URL
/// are accepted by the Settings box as well.
/// </para>
/// </summary>
public static class StremioAddonLink
{
    /// <summary>The protocol Windows hands to Pirsa for an addon link.</summary>
    public const string Scheme = "stremio";

    /// <summary>
    /// Extracts a normalized http(s) manifest URL, or returns false when the
    /// value is not an addon link at all. It does not contact the addon.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out string? manifestUrl)
    {
        manifestUrl = null;

        var link = value?.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(link))
        {
            return false;
        }

        string payload;
        if (link.StartsWith("https://web.stremio.com/", StringComparison.OrdinalIgnoreCase) ||
            link.StartsWith("http://web.stremio.com/", StringComparison.OrdinalIgnoreCase))
        {
            // The addon URL sits in the fragment: #/addons?addon=<encoded>.
            if (!TryReadQueryValue(link, "addon", out payload))
            {
                return false;
            }
        }
        else if (link.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            payload = link[(Scheme.Length + 1)..].TrimStart('/');
            if (payload.Length == 0)
            {
                return false;
            }

            // Only decode when the scheme itself was encoded. Decoding every
            // link would corrupt percent-escapes that belong to the manifest
            // query string, such as a token containing %2B.
            if (IsEncodedUrl(payload))
            {
                try
                {
                    payload = Uri.UnescapeDataString(payload);
                }
                catch (UriFormatException)
                {
                    return false;
                }
            }
        }
        else
        {
            // A manifest URL pasted into Settings, or an http(s) activation
            // for an addon page.
            payload = link;
        }

        payload = payload.Trim();
        if (payload.Length == 0)
        {
            return false;
        }

        if (!payload.Contains("://", StringComparison.Ordinal))
        {
            payload = "https://" + payload.TrimStart('/');
        }

        if (!AddonRegistry.TryNormalizeManifestUrl(payload, out var normalized))
        {
            return false;
        }

        manifestUrl = normalized;
        return true;
    }

    /// <summary>True when the whole payload, scheme included, is percent-encoded.</summary>
    private static bool IsEncodedUrl(string payload) =>
        payload.StartsWith("http%3a", StringComparison.OrdinalIgnoreCase) ||
        payload.StartsWith("https%3a", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds <paramref name="name"/>= in a URL's query or fragment, stopping at
    /// the next &amp; or #.
    /// </summary>
    private static bool TryReadQueryValue(string link, string name, out string value)
    {
        value = "";

        var marker = name + "=";
        var index = link.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var previous = index == 0 ? '\0' : link[index - 1];
            if (previous is '?' or '&' or '#')
            {
                var start = index + marker.Length;
                var end = link.IndexOfAny(['&', '#'], start);
                var raw = end < 0 ? link[start..] : link[start..end];

                try
                {
                    value = Uri.UnescapeDataString(raw);
                }
                catch (UriFormatException)
                {
                    return false;
                }

                return value.Length > 0;
            }

            index = link.IndexOf(marker, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
