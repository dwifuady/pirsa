<img src="assets/icon.svg" width="64" height="64" alt="">

# Pirsa

An unofficial, Stremio-compatible WinUI 3 addon client for movies, shows and
live TV. Pirsa hosts no content: it plays what the third-party
[Stremio addons](https://stremio.github.io/stremio-addon-sdk/) you add return.

**Pirsa** is Javanese. *Mirsa* means **to see** or **watch** — it gives
Indonesian *memirsa* ("to watch") and *pemirsa* ("viewer, audience").

![WinUI 3](https://img.shields.io/badge/WinUI-3-0E7A0D) ![.NET](https://img.shields.io/badge/.NET-10-512BD4)

## Features

- **Store-style shell** with a navigation rail: Home, Discover, Watchlist and
  Settings.
- **Home** — a Top 10 hero, catalog rails from every configured addon, and a
  Continue watching rail.
- **Resume and history** — per-movie and per-episode positions, instant resume
  from the last working source, and Start over. Stored locally.
- **Watched and watchlist** — mark movies, episodes, seasons or whole series as
  watched, and keep a poster-grid watchlist.
- **Discover and search** — browse each addon's catalogs and filters, or search
  every search-capable addon in parallel.
- **Details** — backdrop hero, metadata by addon priority, and season/episode
  browsing.
- **Native player** — libvlc with custom Windows 11-style controls, embedded
  and addon subtitles with live timing, audio tracks, speed, fullscreen,
  autoplay, auto-advance and source switching.
- **Addon manager** — add, enable, disable, reorder and remove Stremio addons.
  Cinemeta and OpenSubtitles v3 are included; no stream addon ships by default.
- **Addon links** — a `stremio://` addon link opens Pirsa with a confirmation
  before installing, and links can be pasted into Settings too.
- **Automatic updates** — every launch checks GitHub Releases. Download the
  new build from the notice, click **Install**, and Pirsa closes, updates, and
  reopens. Installed copies use the per-user installer; portable copies update
  in place.

## Download

Published for `win-x64` and `win-arm64` from
[Releases](https://github.com/dwifuady/pirsa/releases):

- **Installer** — `Pirsa-<version>-win-<arch>-setup.exe`. Per-user install, no
  admin rights, Start menu shortcut, uninstalls from **Apps & Features**.
- **Portable** — `Pirsa-<version>-win-<arch>.zip`. Unpack anywhere and run
  `Pirsa.exe`.

Neither build is code-signed, so Windows SmartScreen may warn on the first run:
**More info → Run anyway**.

Both builds check for a newer release on every launch and can download and
install it from inside the app. Portable copies update in place; installed
copies use the per-user installer and reopen when it finishes.

## Run it

Requires Windows 10 1809+, .NET 10 SDK, WinApp CLI 0.7+ and Developer Mode.

```powershell
cd Pirsa
winapp run . --detach
```

See [Development](docs/DEVELOPMENT.md) for a direct build and release steps.

## Addons

Open **Settings** and paste a Stremio addon manifest URL. Catalogs and streams
from any configured addon are picked up automatically, and the order you set is
the priority order.

A fresh install includes:

```
Cinemeta          https://v3-cinemeta.strem.io/manifest.json
OpenSubtitles v3  https://opensubtitles-v3.strem.io/manifest.json
```

Neither returns video sources. Pirsa does not ship, recommend or list stream
addons — add the ones you trust and are allowed to use. See
[Addons](docs/ADDONS.md) for ordering, filtering, data locations and privacy.

## Documentation

| Document | Covers |
| --- | --- |
| [Features](docs/FEATURES.md) | Everything the app does, in detail, plus limitations |
| [Addons](docs/ADDONS.md) | Addon management, data locations, privacy |
| [Player](docs/PLAYER.md) | Engine, source discovery, stream cache, diagnostics |
| [Development](docs/DEVELOPMENT.md) | Build, run, release, project layout |

## License

Pirsa is free software under the **GNU General Public License v3.0 or later**
(see `LICENSE`). Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) — including libVLC, which is
LGPL-2.1-or-later and ships as unmodified separate binaries.

## Legal

- Pirsa is an unofficial, Stremio-compatible client. It is not affiliated with,
  endorsed by, or connected to Stremio.
- It hosts, indexes, proxies and caches no media and ships no addon directory.
  It ships no stream addon and no torrent engine; only direct HTTP(S) sources
  are played. Catalogs, metadata, subtitles and sources come from third-party
  addons that you add yourself.
- You are responsible for the addons you configure and for using them where you
  have the rights to. Add only addons you trust and are allowed to use.
- If you believe an addon infringes your rights, contact that addon's operator;
  Pirsa has no content to remove. To report a problem with this client, use the
  project's GitHub issue tracker.
- This is a product description, not legal advice.
