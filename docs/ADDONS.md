# Addons

Pirsa speaks the [Stremio addon protocol](https://stremio.github.io/stremio-addon-sdk/)
and hosts no content. Catalogs, metadata, subtitles and stream sources all come
from addons you configure yourself.

## Adding addons

Open **Settings** (gear at the bottom of the left pane) and paste a Stremio
addon manifest URL, for example:

```
https://v3-cinemeta.strem.io/manifest.json
```

A fresh install contains two addons:

```
Cinemeta          https://v3-cinemeta.strem.io/manifest.json
OpenSubtitles v3  https://opensubtitles-v3.strem.io/manifest.json
```

- Catalog resources appear on Home and in Discover on the next visit.
- Stream resources are used by the player.
- Subtitle resources feed the CC picker in the player.
- Each card shows the addon name, version, capabilities, types and host. The
  full manifest URL (which may contain a private token) is stored locally in
  `addons.json` and is deliberately not rendered in the UI.
- `tv`/`channel` catalogs render as **Live TV** rails; the player accepts
  `tv`/`channel`/`pp-live:` ids as well as movies and episodes.

## Addon links

Addon authors and sites share install links such as

```
stremio://v3-cinemeta.strem.io/manifest.json
```

Clicking one opens Pirsa with a confirmation naming the addon's host. Nothing is
installed until you choose **Add addon**; cancelling, using an invalid link, or a
manifest that cannot be loaded leaves your list unchanged. The same links can be
pasted into Settings → Addons instead of a manifest URL, and the web share form
`https://web.stremio.com/#/addons?addon=<encoded manifest URL>` is accepted too.

- Only links that resolve to an `http(s)` manifest are accepted; `file:`,
  `javascript:` and other schemes are rejected.
- The confirmation shows the addon's host, never the full manifest URL, so a
  token in the link or manifest query is not displayed.
- Windows routes `stremio://` through the protocol handler. Packaged builds
  (Store, or a `winapp` debug run) declare it in the app package. The installer
  registers it per user for installed copies. Portable copies use the **Open
  stremio:// addon links in Pirsa** switch in Settings → Addons, which writes the
  same per-user keys; turning it off removes them again.
- With more than one client installed, Windows decides which one receives the
  link. The Settings switch lets this copy claim or release it.
- A link that arrives while Pirsa is running opens a second window; there is no
  single-instance hand-off yet.
- Opening a link contacts that addon's server to read its manifest. Nothing is
  sent anywhere else, and the addon is stored locally like one added by hand.

## Order, enablement and filtering

- **Order is priority.** The first enabled addon wins for metadata details and
  its stream sources are listed first in the player. Addons are asked for
  streams in this order, but a slow one never blocks results that already
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

## Stream addons

- **No stream addon ships by default.** Cinemeta supplies catalogs and metadata
  and OpenSubtitles v3 supplies subtitles; neither returns video sources.
- Play therefore stays visible instead of disappearing: the first press explains
  the setup and offers **Open addon settings**, and later presses go straight
  there.
- Pirsa does not ship, recommend, or list stream addons. The app is fully usable
  without one, and stream sources appear as soon as one is added.

## Where data lives

Configuration lives in the app's local data, and the folder depends on **how the
build is launched**, so a development session can never touch an installed copy:

```
# packaged: Store install, or a dev run through winapp / dotnet run
%LOCALAPPDATA%\Packages\<package-id>\LocalState\        (addons.json, settings.json, …)
%LOCALAPPDATA%\Packages\<package-id>\LocalState\dev\     (Debug builds of the same identity)

# unpackaged: Inno Setup installer, portable ZIP
%LOCALAPPDATA%\Pirsa\                                   (Release build)
%LOCALAPPDATA%\Pirsa-dev\                               (Debug build)
```

Every file lives in that one root: `addons.json`, `settings.json`,
`watch-progress.json`, `watchlist.json`, `search-history.json`,
`subtitle-offsets.json`, `player.log` and the `cache\` folder used while a
video plays. `Services/LocalPaths.cs` is the only place that resolves it, and
the player log header names the root it used.

## Privacy

- If a manifest URL contains a token, treat it like a password: it is only
  stored on this machine, and you should rotate it if it is ever exposed.
- `addons.json` and `watch-progress.json` can both contain private stream URLs
  or tokens. Nothing is sent anywhere except to the addon/provider that supplied
  the source.
- Watch history, watchlist, search history, subtitle timing and settings never
  leave the machine.
