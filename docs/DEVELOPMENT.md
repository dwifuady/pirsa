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
  Services/StreamCacheSession.cs  localhost read-ahead cache that makes seeks local
  Services/PlayerLog.cs           per-session seek/buffering log for diagnosis
  Services/LocalPaths.cs          the one data root: packaged, unpackaged, and Debug runs
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

## Download artifacts

Two build types are published for `win-x64` and `win-arm64`:

- **Installer** — `Pirsa-<version>-win-<arch>-setup.exe`. Per-user install to
  `%LOCALAPPDATA%\Programs\Pirsa`, no admin rights, Start menu shortcut, and it
  uninstalls from **Apps & Features**.
- **Portable** — `Pirsa-<version>-win-<arch>.zip`. Unpack it anywhere and run
  `Pirsa.exe`.

Neither build is code-signed, so Windows SmartScreen may warn on the first run.
