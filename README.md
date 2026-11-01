<img src="assets/icon.svg" width="64" height="64" alt="">

# Pirsa

A native WinUI 3 browser for movies, shows, and live TV sourced from
[Stremio addons](https://stremio.github.io/stremio-addon-sdk/).

![WinUI 3](https://img.shields.io/badge/WinUI-3-0E7A0D) ![.NET](https://img.shields.io/badge/.NET-10-512BD4)

## What it does

- **Store-style shell** — a collapsible left navigation pane with **Home** and
  **Settings**, plus a custom title bar and Mica chrome.
- **Home** — a Top 10 hero carousel (movie/top when available) followed by every
  catalog exposed by the user's configured addons, with the source addon shown
  on each rail.
- **Details** — full-bleed backdrop, poster, overview, fact sheet, IMDb link,
  and season/episode browsing. Metadata is fetched from the addon that supplied
  the catalog item. Posters animate into the details hero with a native
  connected animation.
- **Settings → Addons** — add and remove Stremio addon manifest URLs. A fresh
  install contains **only Cinemeta**; the app is fully usable without any stream
  addon, and stream sources appear as soon as one is added. Addons are persisted
  locally and can be restored to the default at any time.
- **Native player** — LibVLCSharp + libvlc in a WinUI SwapChainPanel, with a
  custom Windows 11-style chrome: seek bar, 10-second skips, volume, speed,
  fullscreen, auto-hiding controls, and a source picker.
- **Playback sources** — every configured addon that declares a `stream`
  resource is queried in parallel; sources are ranked for a sensible default
  (1080p HLS first), with headers from `behaviorHints.proxyHeaders` forwarded to
  VLC when needed.

The default metadata addon is Cinemeta:

```
https://v3-cinemeta.strem.io/manifest.json
```

Catalogs and streams from any configured addon are picked up automatically.
`tv`/`channel` catalogs render as **Live TV** rails; the player accepts
`tv`/`channel`/`pp-live:` ids as well as movies and episodes.

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

- Catalog resources appear on Home on the next visit.
- Stream resources are used by the player.
- Each card shows the addon name, version, capabilities, types and host. The
  full manifest URL (which may contain a private token) is stored locally in
  `addons.json` and is deliberately not rendered in the UI.
- **Remove addon** asks for confirmation. **Restore default** removes custom
  addons and puts Cinemeta back.
- Changing the list bumps the registry revision; Home reloads when you return
  to it.

Configuration lives in the app's local data:

```
%LOCALAPPDATA%\Packages\<package-id>\LocalState\addons.json
```

If a manifest URL contains a token, treat it like a password: it is only stored
on this machine, and you should rotate it if it is ever exposed.

## Choosing the hero catalog

The hero source is intentionally a constant for this version. It looks for
`movie/top` across configured addons and falls back to the first available
catalog. Edit `Pirsa/ViewModels/HomeViewModel.cs`:

```csharp
private const string HeroType = "movie";
private const string HeroCatalogId = "top";
```

## Project map

```
Pirsa/
  Models/StremioModels.cs         manifest, catalog, meta, stream models + image loading
  Models/PlaybackRequest.cs       movie / episode → player routing
  Services/StremioAddonClient.cs  Stremio addon HTTP client
  Services/AddonRegistry.cs       persisted addon list, manifest loading, add/remove
  Services/VideoEngine.cs         background libvlc warm-up
  ViewModels/HomeViewModel.cs     hero + rails across addons
  ViewModels/DetailsViewModel.cs  meta routing, facts, seasons, episodes
  ViewModels/PlayerViewModel.cs   stream discovery, ranking, dedupe
  ViewModels/SettingsViewModel.cs addon manager state
  Views/HomePage.xaml             hero carousel + catalog rails
  Views/DetailsPage.xaml          backdrop hero + facts + episodes
  Views/PlayerPage.xaml           LibVLC surface + transport chrome
  Views/SettingsPage.xaml         addons + about
  MainWindow.xaml                 title bar + NavigationView shell
  Themes/Brand.xaml               Light / Dark / HighContrast palette and styles
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
- Keyboard: **F** fullscreen, **M** mute, **Esc** exits fullscreen/closes the
  source panel. Space/Enter activate the focused transport button.
- The source picker switches sources while keeping the current playback
  position.
- Source discovery can take 10–20 seconds depending on the addon/provider; the
  loading screen stays up until sources arrive.

The native libvlc binaries add roughly 150–200 MB to the packaged app and are
licensed LGPL-2.1; review your distribution obligations if you publish it.

## Limitations (initial version)

- No subtitle-track UI yet (the addon `subtitles` resource is not wired).
- No audio-track picker, episode auto-advance, or watch progress.
- No addon enable/disable toggle; addons are either configured or removed.
- Torrent `infoHash` streams and `externalUrl`-only entries are skipped; only
  direct HTTP(S) sources are played.
