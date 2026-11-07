namespace Pirsa.Helpers;

/// <summary>One row in the Settings search results.</summary>
public sealed record SettingsSearchEntry(
    string PageTag,
    string Title,
    string Detail,
    string Anchor,
    string Keywords)
{
    /// <summary>Keeps the ListViewItem's automation name readable.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The hand-maintained index behind Settings search. Anchors are x:Name values
/// on the target page; the shell scrolls the row into view after navigation.
/// </summary>
public static class SettingsSearchIndex
{
    private static readonly SettingsSearchEntry[] All =
    [
        new("general", "Theme", "General · Appearance", "ThemeRow",
            "appearance dark light system color"),
        new("general", "Open Pirsa on", "General · Startup", "StartPageRow",
            "start page startup home discover watchlist"),
        new("general", "Check for updates automatically", "General · Updates", "UpdateRow",
            "update github release version auto check"),
        new("general", "Version", "General · Updates", "VersionRow",
            "version build update"),

        new("playback", "Start playing automatically", "Playback · When you press Play", "AutoplayRow",
            "autoplay automatic source picker start play"),
        new("playback", "Preferred quality", "Playback · When you press Play", "QualityRow",
            "quality 4k 1440p 1080p 720p 480p resolution best available"),
        new("playback", "Play next episode automatically", "Playback · Between episodes", "AutoAdvanceRow",
            "next episode auto advance series"),
        new("playback", "Wait before the next episode", "Playback · Between episodes", "AutoAdvanceDelayRow",
            "countdown delay up next seconds credits auto advance"),
        new("playback", "Preferred audio language", "Playback · Audio", "AudioLanguageRow",
            "audio language track dub original english spanish french"),
        new("playback", "Resume where you stopped", "Playback · Resume", "ResumeRow",
            "resume continue position start over history"),
        new("playback", "Skip intro and outro", "Playback · Skipping", "SkipSegmentsRow",
            "skip intro outro recap credits opening ending segments"),
        new("playback", "Skip intros automatically", "Playback · Skipping", "AutoSkipIntroRow",
            "auto skip intro opening automatically"),
        new("playback", "Skip recaps automatically", "Playback · Skipping", "AutoSkipRecapRow",
            "auto skip recap previously on automatically"),
        new("playback", "Skip outros and credits automatically", "Playback · Skipping", "AutoSkipOutroRow",
            "auto skip outro ending credits post-credits automatically"),
        new("playback", "Video engine", "Playback · Advanced", "EngineRow",
            "mpv vlc libvlc engine player backend"),

        new("subtitles", "Preferred subtitle language", "Subtitles · Language", "LanguageRow",
            "subtitles language cc captions english off"),
        new("subtitles", "Subtitle text size", "Subtitles · Appearance", "SubtitleTextSizeRow",
            "subtitle size text font large small appearance"),
        new("subtitles", "Subtitle background", "Subtitles · Appearance", "SubtitleBackgroundRow",
            "subtitle background dim solid contrast appearance"),
        new("subtitles", "Saved subtitle timing", "Subtitles · Advanced", "ClearTimingRow",
            "subtitle timing offset delay clear reset per title"),

        new("library", "Home catalog rows", "Library & data · Home", "HomeRowsRow",
            "home rows catalogs posters order pin"),
        new("library", "Watch history", "Library & data · History", "WatchHistoryRow",
            "watch history resume positions clear continue watching"),
        new("library", "Search history", "Library & data · History", "SearchHistoryRow",
            "search history suggestions clear"),
        new("library", "Data folder", "Library & data · This PC", "DataFolderRow",
            "data folder storage local files open explorer"),
        new("library", "Stream cache", "Library & data · Storage", "CacheSizeRow",
            "cache storage temporary disk clear"),
        new("library", "Player log", "Library & data · Storage", "LogSizeRow",
            "player log diagnostics storage open"),
        new("library", "Reset all settings", "Library & data · Advanced", "ResetSettingsRow",
            "reset settings defaults restore preferences"),

        new("addons", "Add-ons", "Add-ons", "AddonsSummary",
            "addons add-on manifest cinemeta opensubtitles stream metadata catalog"),
        new("addons", "Add add-on", "Add-ons", "AddAddonButton",
            "add add-on install manifest url stremio link"),
        new("addons", "Open stremio:// links in Pirsa", "Add-ons · Advanced", "LinkHandlingRow",
            "stremio link protocol handler default app"),
        new("addons", "Restore default add-ons", "Add-ons", "RestoreDefaultsButton",
            "restore reset defaults cinemeta opensubtitles"),

        new("sync", "Sync providers", "Sync", "",
            "sync simkl mdblist webdav watched watchlist devices"),

        new("about", "Version", "About", "VersionRow",
            "version build about"),
        new("about", "License", "About · Resources", "LicenseRow",
            "license gpl source github"),
        new("about", "Third-party notices", "About · Resources", "ThirdPartyRow",
            "third party notices mpv vlc libvlc credits"),
        new("about", "Player log", "About · Diagnostics", "AboutLogRow",
            "player log diagnostics bug report open"),
        new("about", "System information", "About · Diagnostics", "SystemInfoRow",
            "system information copy version windows architecture engine"),
        new("about", "Data folder", "About · Diagnostics", "AboutDataFolderRow",
            "data folder files storage open"),
    ];

    public static IReadOnlyList<SettingsSearchEntry> Search(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        return All
            .Where(entry =>
                entry.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                entry.Detail.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                entry.Keywords.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
