<img src="assets/icon.svg" width="64" height="64" alt="">

# Pirsa

An unofficial, Stremio-compatible WinUI 3 addon client for movies, shows,
and live TV. Pirsa hosts no content: it plays what the third-party
[Stremio addons](https://stremio.github.io/stremio-addon-sdk/) you add return.

![WinUI 3](https://img.shields.io/badge/WinUI-3-0E7A0D) ![.NET](https://img.shields.io/badge/.NET-10-512BD4)

## What it does

- **Store-style shell** — an icon-only navigation rail with **Home**,
  **Discover**, **Watchlist** and **Settings** by default. The title-bar
  hamburger expands it to labels as an overlay; the rail hides completely while
  a video is playing.
- **Home** — a Top 10 hero carousel (movie/top when available) followed by every
  catalog exposed by the user's configured addons, with the source addon shown
  on each rail.
- **Continue watching** — a rail above the catalogs with the movies and series
  you didn't finish, newest first. Each card shows how much is left (and, for a
  series, which episode). Selecting a card resumes playback where you stopped;
  right-click a card to remove a title from the rail.
- **Instant resume** — the stream source that last reached *playing* is remembered
  with the position, so Continue watching starts it immediately instead of
  showing the source picker. Source discovery runs in parallel in case it died:
  a URL that doesn't play within 15 seconds is forgotten and the best freshly
  found source takes over. Remembered sources are preferred for 24 hours; after
  that Pirsa searches normally.
- **Resume playback** — every movie and episode keeps its own saved position in
  a local watch history. The player offers **Start over** while the resume hint
  is visible, the details page turns its Play button into **Resume S02E04** (or
  **Resume 23:41** for a movie), and episode rows show **Watched** or
  **Resume 12:34** with a thin progress bar. History never leaves the machine.
- **Watchlist** — every movie and series details page has an **Add to
  watchlist** button (it turns into **In watchlist**, and pressing it again
  removes the title). Saved titles appear on the **Watchlist** page as a poster
  grid, newest first, with their added date; select a card to reopen details or
  right-click it to remove it. The page also offers **Clear watchlist**. The
  list is stored on this device only.
- **Discover** — browse what your addons provide without typing: pick a **type**
  (All, Movies, Series, Live TV, or any custom type an addon declares), a
  **catalog** from those addons, and the catalog's own **genre/year filter**.
  Browse results load as a poster grid with **Load more** paging via `skip`.
- **Search** — the search box sits centered in the title bar on every page.
  It searches **every addon that declares a `search` extra**, in parallel — not
  just the selected catalog — and groups the hits by **addon + media type**
  (Cinemeta · Movies, Cinemeta · Series), with a title count per section. The
  type/catalog/genre dropdowns apply to browsing only and hide during search.
  Typing from Home or Settings switches to Discover automatically. Submitted
  searches (and searches you open a result from) are remembered locally and
  offered as suggestions when the search box is focused, filtered as you type.
  Clear them in Settings → Search.
- **Details** — full-bleed backdrop, poster, overview, fact sheet, IMDb link,
  and season/episode browsing. Metadata follows your addon priority order: the
  first enabled metadata addon that understands the title's id supplies it,
  with the next addon as a fallback. Custom id prefixes only match the addon
  that declared them. Posters animate into the details hero with a native
  connected animation. Opening a title also **warms stream discovery in the
  background** for the episode Play would start (and for an episode you hover),
  so Play shows the source list — or starts playing — without waiting for the
  addons.
- **Settings → Addons** — add, remove, enable/disable, and reorder Stremio
  addon manifest URLs. A fresh install contains **Cinemeta and OpenSubtitles
  v3**; the app is fully usable without any stream addon, and stream sources
  appear as soon as one is added. With more than five addons, a search box
  (name or host) and capability filters appear. Addons are persisted locally
  and can be restored to the defaults at any time.
- **Native player** — LibVLCSharp + libvlc in a WinUI SwapChainPanel, with a
  custom Windows 11-style chrome: seek bar, 10-second skips, volume, speed,
  fullscreen, auto-hiding controls, a source picker, and a **CC subtitle
  picker**.
- **Playback sources** — every enabled addon that declares a `stream` resource
  is queried in your addon order, but the picker fills in as answers arrive:
  one slow addon never delays the sources others already returned. Duplicate
  URLs keep the entry from the higher-priority addon, and pressing Play can
  start the first source that arrives without waiting for the rest. By default,
  pressing Play opens the picker as soon as something is available; choose a
  source to start. Headers from `behaviorHints.proxyHeaders` are forwarded to
  VLC when needed.

A fresh install includes two addons:

```
Cinemeta          https://v3-cinemeta.strem.io/manifest.json
OpenSubtitles v3  https://opensubtitles-v3.strem.io/manifest.json
```

Catalogs and streams from any configured addon are picked up automatically.
`tv`/`channel` catalogs render as **Live TV** rails; the player accepts
`tv`/`channel`/`pp-live:` ids as well as movies and episodes.

## Download

Two build types are published for `win-x64` and `win-arm64`:

- **Installer** — `Pirsa-<version>-win-<arch>-setup.exe`. Per-user install to
  `%LOCALAPPDATA%\Programs\Pirsa`, no admin rights, Start menu shortcut, and
  it uninstalls from **Apps & Features**.
- **Portable** — `Pirsa-<version>-win-<arch>.zip`. Unpack it anywhere and run
  `Pirsa.exe`.

Download from [Releases](https://github.com/dwifuady/pirsa/releases). Neither
build is code-signed, so Windows SmartScreen may warn on the first run:
**More info → Run anyway**.

## Run it

Requirements:

- Windows 10 1809+ (Windows 11 recommended for Mica and Segoe UI Variable)
- .NET 10 SDK
- [WinApp CLI](https://github.com/microsoft/winappCli) 0.7+
- Developer Mode enabled

```powershell
cd Pirsa
winapp run . --detach
```

Or build directly:

```powershell
dotnet build Pirsa.csproj -p:Platform=x64
```

The app is packaged; use `winapp run` so it launches with package identity.

## Addons

Open **Settings** (gear at the bottom of the left pane) and paste a Stremio
addon manifest URL, for example:

```
https://v3-cinemeta.strem.io/manifest.json
```

- Catalog resources appear on Home and in Discover on the next visit.
- Stream resources are used by the player.
- Subtitles resources feed the CC picker in the player.
- Each card shows the addon name, version, capabilities, types and host. The
  full manifest URL (which may contain a private token) is stored locally in
  `addons.json` and is deliberately not rendered in the UI.
- **Order is priority.** The first enabled addon wins for metadata details and
  its stream sources are listed first in the player. Addons are asked for
  streams in this order, but a slow one never blocks the results that already
  arrived. Use the ▲/▼ buttons to move a row; it applies immediately, including
  to a source list that is already open. Reordering is disabled while the list
  is filtered, so rows cannot jump under the pointer.
- **Enable/disable** keeps an addon configured but stops all of its catalogs,
  streams, metadata and subtitles. Disabled addons keep their position in the
  list, so turning one back on restores its priority.
- **Search and filters** appear once more than five addons are configured:
  match by name or host, then narrow to Streams, Metadata, Catalogs, or Issues
  (addons whose manifest failed to load).
- **Remove addon** asks for confirmation. Disabling or removing the addon that
  supplied details announces which addon takes over.
- **Restore default** removes custom addons and restores Cinemeta and
  OpenSubtitles v3, resetting the order to the defaults.
- Changing the list bumps the registry revision; Home and Discover reload when
  you return to them, and an open player re-ranks its sources.
- **No stream addon ships by default.** Cinemeta supplies catalogs and metadata
  and OpenSubtitles v3 supplies subtitles; neither returns video sources. Play
  therefore stays visible instead of disappearing: the first press explains the
  setup and offers **Open addon settings**, and later presses go straight
  there. Pirsa does not ship, recommend, or list stream addons.

Configuration lives in the app's local data:

```
%LOCALAPPDATA%\Packages\<package-id>\LocalState\addons.json
%LOCALAPPDATA%\Packages\<package-id>\LocalState\settings.json
%LOCALAPPDATA%\Packages\<package-id>\LocalState\watch-progress.json
%LOCALAPPDATA%\Packages\<package-id>\LocalState\watchlist.json
%LOCALAPPDATA%\Packages\<package-id>\LocalState\search-history.json
```

A portable (unpackaged) build keeps the same files one level up:

```
%LOCALAPPDATA%\Pirsa\addons.json
%LOCALAPPDATA%\Pirsa\settings.json
%LOCALAPPDATA%\Pirsa\watch-progress.json
%LOCALAPPDATA%\Pirsa\watchlist.json
%LOCALAPPDATA%\Pirsa\search-history.json
```

If a manifest URL contains a token, treat it like a password: it is only stored
on this machine, and you should rotate it if it is ever exposed.

## License

Pirsa is free software under the **GNU General Public License v3.0 or later**
(see `LICENSE`). Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) — including libVLC, which is
LGPL-2.1-or-later and ships as unmodified separate binaries.

## Legal

- Pirsa is an unofficial, Stremio-compatible client. It is not affiliated with,
  endorsed by, or connected to Stremio.
- It hosts, indexes, proxies, and caches no media and ships no addon directory.
  It ships no stream addon and no torrent engine; only direct HTTP(S) sources
  are played. Catalogs, metadata, subtitles, and sources come from third-party
  addons that you add yourself.
- You are responsible for the addons you configure and for using them where you
  have the rights to. Add only addons you trust and are allowed to use.
- If you believe an addon infringes your rights, contact that addon's operator;
  Pirsa has no content to remove. To report a problem with this client, use the
  project's GitHub issue tracker.
- This is a product description, not legal advice.

## Subtitles

- The **CC** button in the player opens a two-step picker:
  1. **Off** plus one row per language. Languages are grouped across all
     subtitle addons and sorted by how many tracks are available.
  2. For a language with multiple tracks, a version list opens. Every row shows
     the release group, file name, addon, frame rate and encoding, so you can
     pick the version that matches your release. The active version is shown
     above the list and remains selected when you reopen the picker.
- Languages with a single track apply immediately. **Off** disables subtitles
  immediately. Selecting a different version is how you test subtitle sync.
- **Preferred subtitle language** (Settings → Playback) turns the first
  matching track on automatically for every title and episode, including the
  next episode after auto-advance. Picking a language in the player remembers it
  as the preference, while choosing **Off** there only applies to the title you
  are watching. Two- and three-letter codes (en / eng) count as the same
  language.
- The current subtitle stays selected across source switches.
- Subtitle files are downloaded with .NET first and handed to VLC as local
  files. This avoids a libvlc 3.0.24 GnuTLS crash that can occur when remote
  subtitle downloads overlap with stream TLS sessions.
- Adding another subtitle addon in Settings automatically adds its languages to
  the same picker.
- Embedded subtitle tracks are not listed yet; only addon-provided tracks.

## Choosing the hero catalog

The hero source is intentionally a constant for this version. It looks for
`movie/top` across configured addons and falls back to the first available
catalog. Edit `Pirsa/ViewModels/HomeViewModel.cs`:

```csharp
private const string HeroType = "movie";
private const string HeroCatalogId = "top";
```

## Releasing

The git tag is the version. Tag a commit and push it; CI builds both
architectures as portable ZIPs and Inno Setup installers, then publishes them
with SHA-256 checksums.

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Keep `Version` in `Pirsa/Pirsa.csproj` in sync for local builds; the workflow
overrides it from the tag.

## Project map

```
installer/
  Pirsa.iss                     Inno Setup script for the per-user installer
Pirsa/
  Models/StremioModels.cs         manifest, catalog, meta, stream models + image loading
  Models/PlaybackRequest.cs       movie / episode → player routing
  Services/StremioAddonClient.cs  Stremio addon HTTP client
  Services/AddonRegistry.cs       persisted addon list, manifest loading, add/remove
  Services/StreamDiscovery.cs     per-title stream fetch cache + details-page prefetch
  Services/WatchProgressStore.cs  local watch history (movie/episode position, resume)
  Services/VideoEngine.cs         background libvlc warm-up
  Helpers/SubtitleLanguages.cs    ISO 639 language names for the CC picker
  ViewModels/HomeViewModel.cs     hero + rails across addons
  ViewModels/DiscoverViewModel.cs type/catalog/genre filters, search and paging
  ViewModels/DetailsViewModel.cs  meta routing, facts, seasons, episodes
  ViewModels/PlayerViewModel.cs   stream discovery, ranking, dedupe
  ViewModels/SettingsViewModel.cs addon manager state
  Views/HomePage.xaml             hero carousel + catalog rails
  Views/DiscoverPage.xaml         catalog browser filters, search and poster grid
  Views/DetailsPage.xaml          backdrop hero + facts + episodes
  Views/PlayerPage.xaml           LibVLC surface + transport chrome
  Views/SettingsPage.xaml         addons + about
  MainWindow.xaml                 title bar + NavigationView shell
  Themes/Brand.xaml               Light / Dark / HighContrast palette and styles
  Themes/PosterCard.xaml          shared poster card template + grid item style
  Assets/                         Pirsa app icon, tiles, splash, title-bar mark
```

The Pirsa mark is generated from `assets/icon.svg`; icon and tile PNGs live in
`Pirsa/Assets`.

## Player

- Engine: **LibVLCSharp.WinUI** with native **libvlc 3.0.24**. VLC is used
  because these addon sources are commonly 4K HEVC/HDR MKV, HLS, or DASH;
  MediaPlayerElement would fail on HEVC without extra codec packages.
- The video surface is a WinUI `SwapChainPanel`, so the transport chrome is
  regular XAML (it stays native, themeable, and keyboard accessible).
- Source discovery queries every configured addon that declares a `stream`
  resource for the media type and id prefix, deduplicates URLs, and ranks 1080p
  HLS sources first. Provider request headers are forwarded as VLC media options.
- **Autoplay** — Settings → Playback has a single **Start playing automatically**
  switch, off by default. When on, choose a **Preferred quality**: Best
  available, 4K, 1440p, 1080p, 720p or 480p. Quality labels come from each
  addon's source metadata; sources that don't declare a resolution are shown as
  **Unknown**. If the preferred quality isn't available, Pirsa uses the best
  available source. With the switch off, every title opens the source picker.
  No regex or advanced syntax.
- **Auto-advance** — Settings → Playback → **Play next episode automatically**
  (default on). The transition keeps using the fastest path: a fresh remembered
  source for the next episode starts instantly, otherwise the best found source
  is used even when the source picker is normally shown. Turning this off keeps
  the Up next card, so episodes can still be skipped by hand.
- Keyboard: **Space** play/pause, **Left/Right** seek ±10 seconds, **F**
  fullscreen, **M** mute, **Esc** exits fullscreen or closes the source/subtitle
  panels. Enter activates a focused button. Arrow keys adjust volume when the
  volume slider itself has focus.
- The source picker switches sources while keeping the current playback
  position.
- **Seek cache** — VLC's demuxer cache is set to **3 seconds** for on-demand
  titles and **2.5 seconds** for live TV (kept shorter so it stays near the live
  edge). It is a jitter buffer, not a seek window: VLC answers every seek with a
  fresh range request to the provider, and the buffering percentage is measured
  against this target, so a bigger value means a *longer* wait after each skip. A
  genuine ±10s seek window needs a local read-ahead cache in front of VLC.
- **Honest buffering** — the overlay follows the playhead, not VLC's buffering
  percentage. VLC reports "Playing" over a frozen picture while it climbs from
  0% to 100% over several seconds, and keeps reporting low percentages after a
  seek that cost nothing. The overlay now appears only after the position has
  stood still for 1.2 seconds, and disappears the moment frames move again.
- **Player log** — `%LOCALAPPDATA%\Pirsa\player.log` records every seek, how long
  the playhead stayed frozen afterwards, the source format and options, and
  VLC's own stream-layer messages. It is reset when the player opens, capped at
  1 MB, and never leaves the machine.
- Source discovery can take 10–20 seconds depending on the addon/provider; the
  loading screen stays up until sources arrive. Results are cached per
  movie/episode for 10 minutes (empty results for 1 minute) and dropped when the
  addon list changes; the details page prefetches them, so Play normally opens
  the picker instantly.

The native libvlc binaries add roughly 150–200 MB to the packaged app and are
licensed LGPL-2.1; review your distribution obligations if you publish it.

## Watch history

- Positions are saved locally in `watch-progress.json`, one row per movie or
  episode (`metaId`, `videoId`, season/episode, position, duration, completed).
  The last stream source that actually played for that row is stored too: its
  URL, addon, quality and request headers. **Treat that file like
  `addons.json`: stream URLs can embed private tokens.** Nothing is sent
  anywhere except to the addon/provider that supplied the source.
- A position is written every 10 seconds while playing, on pause, on source
  switches, when you leave the player, and when a title finishes. Opening a
  title for a few seconds is ignored, and an existing position is never rewound
  by a little playback.
- A title counts as **Watched** at 95%; it leaves Continue watching and the
  details page shows the episode as watched.
- **Instant resume:** with **Start playing automatically** on, a fresh (≤24h)
  remembered source starts without waiting for source discovery. Discovery
  still runs in the background, so if the source fails before playing (error or
  15-second watchdog) it is removed from the history and the next best source
  starts — no error screen. A source is only remembered after it reached
  *playing*, and it is forgotten if its addon was removed.
- A source older than 24 hours is not tried first; Pirsa searches as usual. This
  is a preference window, not a validity check — URLs are always verified by
  actually playing them.
- Player: **Start over** next to the resume hint restarts from 0 without
  deleting the history.
- Settings → Playback → **Watch history** shows how many positions are stored
  and clears them all, including remembered sources.
- The history holds at most 500 entries; the oldest are dropped.

## Limitations (initial version)

- Embedded subtitle tracks are not exposed in the CC list; only addon tracks.
- No audio-track picker. Auto-advance stops at the end of the last known
  episode and does not know about unaired episodes.
- Watch history is per device; there is no sync between machines.
- Addon order is global: per-type or per-catalog priorities are not supported
  yet.
- Torrent `infoHash` streams and `externalUrl`-only entries are skipped; only
  direct HTTP(S) sources are played.
