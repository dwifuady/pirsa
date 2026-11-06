# Development

## Requirements

- Windows 10 1809+ (Windows 11 recommended for Mica and Segoe UI Variable)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [WinApp CLI](https://github.com/microsoft/winappCli) 0.7+
- Developer Mode enabled

## Build and run

```powershell
cd Pirsa
winapp run . --detach
```

Or build directly:

```powershell
dotnet build Pirsa.csproj -p:Platform=x64
```

The app is packaged; use `winapp run` so it launches with package identity.

## Configuration and data

The data root depends on the build type. See
[ADDONS.md](ADDONS.md#where-data-lives) for the exact paths and the files stored
there.

## Choosing the hero catalog

The hero source is intentionally a constant for this version. It looks for
`movie/top` across configured addons and falls back to the first available
catalog. Edit `Pirsa/ViewModels/HomeViewModel.cs`:

```csharp
private const string HeroType = "movie";
private const string HeroCatalogId = "top";
```

## Testing addon links

Packaged runs get the `stremio://` handler from `Package.appxmanifest` once
`winapp run` has registered the debug identity. With the app running:

```powershell
start stremio://v3-cinemeta.strem.io/manifest.json
```

A link for a bundled addon shows **That addon is already configured**; a new
addon shows the confirmation with its manifest details once they arrive.

An unpackaged build has no package identity, so it uses the per-user
`HKCU\Software\Classes\stremio` keys. The installer writes them, or for a
portable copy use Settings → Addons → **Open stremio:// links in Pirsa**.
A direct command-line launch exercises the same path:

```powershell
.\Pirsa.exe "stremio://example.com/manifest.json"
```

There is no single-instance hand-off yet, so activating a link while Pirsa is
running starts a second process.

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

The in-app updater reads the latest release through the GitHub API and expects
the asset names the workflow already produces:
`Pirsa-<version>-win-<arch>-setup.exe`, `Pirsa-<version>-win-<arch>.zip`, and
`SHA256SUMS.txt`. Renaming or dropping one of those assets disables the update
path that needs it. The installer accepts `/RELAUNCH=1`, which the app passes
when it starts a silent update so the new version opens when it is done.

The release must be readable without signing in: an anonymous request to a
private repository returns 404, and the check is silently skipped. Make the
repository (and its releases) public before shipping the updater, or change
`UpdateService.Repository` to a public repository with the same asset layout.

## Design principles

Keep the app simple to use. A feature may add behavior, but a setting is a
decision handed back to the user, so add one only when it answers a question
the app cannot.

- **Connect, don't tune.** Accounts, server URLs, API keys and install
  locations qualify — the app cannot know them. Ranking matrices, quality
  weights and timing knobs do not: choose a good default instead.
- **Never make the user arbitrate between addons.** Order is priority; every
  other choice is inferred. If a preference truly cannot be inferred, remember
  what the user picked last and apply it quietly.
- **Count the promises.** A setting must be tested, documented, migrated and
  kept working. An on/off account connection usually costs less than a
  five-value matrix, so judge a new surface that way, not by how many panels it
  adds.

## Project map

```
installer/
  Pirsa.iss                     Inno Setup script for the per-user installer
Pirsa/
  Models/StremioModels.cs         manifest, catalog, meta, stream models + image loading
  Models/PlaybackRequest.cs       movie / episode → player routing
  Services/StremioAddonClient.cs  Stremio addon HTTP client
  Services/AddonRegistry.cs       persisted addon list, manifest loading, add/remove
  Services/StremioAddonLink.cs    stremio:// and web share links → manifest URL
  Services/StremioProtocolRegistration.cs  per-user stremio:// handler keys
  Services/StreamDiscovery.cs     per-title stream fetch cache + details-page prefetch
  Services/StreamCacheSession.cs  localhost read-ahead cache that makes seeks local
  Services/PlayerLog.cs           per-session seek/buffering log for diagnosis
  Services/LocalPaths.cs          the one data root: packaged, unpackaged, and Debug runs
  Services/WatchProgressStore.cs  local watch history (movie/episode position, resume)
  Services/SubtitleOffsetStore.cs per-title subtitle timing, in subtitle-offsets.json
  Services/SubtitleChoiceStore.cs per-title subtitle language, in subtitle-choices.json
  Services/UpdateService.cs        update check, download, checksum, install hand-off
  Playback/IPlaybackEngine.cs     player backend seam: surface, transport, tracks
  Playback/LibVlc/                libVLC backend and background warm-up
  Helpers/SubtitleLanguages.cs    ISO 639 language names for the CC picker
  Helpers/AddonLinkPrompt.cs      confirmation + install for an activated link
  ViewModels/HomeViewModel.cs     hero + rails across addons
  ViewModels/DiscoverViewModel.cs type/catalog/genre filters, search and paging
  ViewModels/DetailsViewModel.cs  meta routing, facts, seasons, episodes
  ViewModels/PlayerViewModel.cs   stream discovery, ranking, dedupe
  ViewModels/SettingsViewModel.cs addon manager state
  ViewModels/UpdateViewModel.cs   launch update notice, download and install state
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

## Download artifacts

Two build types are published for `win-x64` and `win-arm64`:

- **Installer** — `Pirsa-<version>-win-<arch>-setup.exe`. Per-user install to
  `%LOCALAPPDATA%\Programs\Pirsa`, no admin rights, Start menu shortcut, and it
  uninstalls from **Apps & Features**.
- **Portable** — `Pirsa-<version>-win-<arch>.zip`. Unpack it anywhere and run
  `Pirsa.exe`.

Neither build is code-signed, so Windows SmartScreen may warn on the first run.
