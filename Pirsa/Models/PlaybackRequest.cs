namespace Pirsa.Models;

/// <summary>
/// Everything the player needs to open the right stream: the media type, the
/// Stremio video id (movie id, or series:id:season:episode), display labels,
/// and the artwork to show while sources load.
/// </summary>
public sealed record PlaybackRequest(
    string Type,
    string VideoId,
    string Title,
    string Subtitle,
    MetaItem Meta,
    string? EpisodeTitle = null,
    int Season = 0,
    int Episode = 0)
{
    public static PlaybackRequest ForMeta(MetaItem meta, MetaVideo? episode = null)
    {
        if (!string.IsNullOrWhiteSpace(meta.Type) &&
            (meta.Type.Equals("series", StringComparison.OrdinalIgnoreCase) ||
             meta.Type.Equals("tv", StringComparison.OrdinalIgnoreCase)))
        {
            var selected = episode
                ?? meta.Videos?.FirstOrDefault(v => v.Season > 0 && v.EpisodeNumber > 0)
                ?? meta.Videos?.FirstOrDefault();

            if (selected is not null)
            {
                return ForEpisode(meta, selected);
            }
        }

        return new PlaybackRequest(
            string.IsNullOrWhiteSpace(meta.Type) ? "movie" : meta.Type,
            meta.Id,
            meta.Name,
            meta.HeroMetaLine,
            meta);
    }

    public static PlaybackRequest ForEpisode(MetaItem series, MetaVideo episode) =>
        new(
            string.IsNullOrWhiteSpace(series.Type) ? "series" : series.Type,
            $"{series.Id}:{episode.Season}:{episode.EpisodeNumber}",
            series.Name,
            $"S{episode.Season:00}E{episode.EpisodeNumber:00} · {episode.Title}",
            series,
            episode.Title,
            episode.Season,
            episode.EpisodeNumber);
}
