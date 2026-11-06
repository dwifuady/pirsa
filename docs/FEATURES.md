# Features

Everything Pirsa does, in detail. For install and build steps see
[DEVELOPMENT.md](DEVELOPMENT.md); for addon management see
[ADDONS.md](ADDONS.md); for playback internals see [PLAYER.md](PLAYER.md).

## Shell and navigation

- Icon-only navigation rail with **Home**, **Discover**, **Watchlist** and
  **Settings** by default.
- The title-bar hamburger expands the rail to labels as an overlay.
- The rail hides completely while a video is playing.

## Home

- A Top 10 hero carousel (movie/top when available) followed by every catalog
  exposed by the configured addons.
- The source addon is shown on each rail.
- **Continue watching** sits above the catalogs: the movies and series you
  didn't finish, newest first. Each card shows how much is left (and, for a
  series, which episode).
- Selecting a card resumes playback where you stopped; right-click a card to
  remove a title from the rail.

## Resume and watch history

- Every movie and episode keeps its own saved position in a local watch
  history. Nothing leaves the machine.
- The player offers **Start over** while the resume hint is visible.
- The details page turns its Play button into **Resume S02E04** (or
  **Resume 23:41** for a movie), and episode rows show **Watched** or
  **Resume 12:34** with a thin progress bar.
- A title counts as **Watched** at 95% and leaves Continue watching.
- Positions are written every 10 seconds while playing, on pause, on source
  switches, when you leave the player, and when a title finishes. Opening a
  title for a few seconds is ignored, and a small bit of playback never rewinds
  an existing position.
- Settings → Playback → **Watch history** shows how many positions are stored
  and clears them all, including remembered sources and subtitle timing.
- The history holds at most 500 entries; the oldest are dropped.
- History is local by default; connecting a sync provider mirrors watched state
  and the watchlist to another device (see [Sync](#sync)).

### Instant resume

- The stream source that last reached *playing* is remembered along with the
  position, so Continue watching can start it immediately instead of showing
  the source picker.
- Source discovery still runs in parallel in case the remembered source died: a
  URL that doesn't play within 15 seconds is forgotten and the best freshly
  found source takes over. There is no error screen.
- Remembered sources are preferred for 24 hours; after that Pirsa searches
  normally. This is a preference window, not a validity check — URLs are always
  verified by actually playing them.
- A source is only remembered after it reached *playing*, and it is forgotten
  if its addon was removed.
- With **Start playing automatically** on, a fresh remembered source starts
  without waiting for source discovery. With it off, the source picker opens
  with the remembered source ranked first.
- **Start over** restarts from 0 without deleting the history.

## Watched state

- Movies, single episodes, a season, a hand-picked set of episodes, or a whole
  series can be marked watched (or unwatched) from the details page.
- The hero menu covers the title and the selected season.
- **Select episodes** turns the episode list into checkboxes for a bulk mark.
- Each episode row has a right-click **Mark as watched** action.
- Watched titles carry a check badge on Home, Discover and Watchlist posters.
- A series shows how many episodes are done (for example **3/10 watched**) and
  only reads **Watched** once every listed episode is finished.
- Playback still marks a title watched automatically when it finishes, and a
  later rewatch keeps the watched date.
- When a partly watched series has no unfinished episode, the next unwatched
  episode is offered in **Continue watching** (for example after marking
  episodes 1–8 of 10, episode 9 appears there).
- Watched state is stored beside the resume positions on this device only.

## Watchlist

- Every movie and series details page has an **Add to watchlist** button. It
  turns into **In watchlist**, and pressing it again removes the title.
- Saved titles appear on the **Watchlist** page as a poster grid, newest first,
  with their added date.
- Select a card to reopen details or right-click it to remove it.
- The page also offers **Clear watchlist**.
- The list is stored on this device only.

## Discover

- Browse what your addons provide without typing: pick a **type** (All, Movies,
  Series, Live TV, or any custom type an addon declares), a **catalog** from
  those addons, and the catalog's own **genre/year filter**.
- Browse results load as a poster grid with **Load more** paging via `skip`.

## Search

- The search box sits centered in the title bar on every page.
- It searches **every addon that declares a `search` extra**, in parallel — not
  just the selected catalog — and groups the hits by **addon + media type**
  (Cinemeta · Movies, Cinemeta · Series), with a title count per section.
- The type/catalog/genre dropdowns apply to browsing only and hide during
  search.
- Typing from Home or Settings switches to Discover automatically.
- Submitted searches (and searches you open a result from) are remembered
  locally and offered as suggestions when the search box is focused, filtered
  as you type. Clear them in Settings → Search.

## Details

- Full-bleed backdrop with a slow drift, poster, overview, fact sheet, IMDb
  link, and season/episode browsing.
- The drift pauses while the hero is scrolled away, and never starts when
  Windows is set to reduce motion or a High Contrast theme is active.
- Metadata follows your addon priority order: the first enabled metadata addon
  that understands the title's id supplies it, with the next addon as a
  fallback. Custom id prefixes only match the addon that declared them.
- Posters animate into the details hero with a native connected animation.
- Opening a title **warms stream discovery in the background** for the episode
  Play would start (and for an episode you hover), so Play shows the source list
  — or starts playing — without waiting for the addons.

## Native player

- libVLC or mpv in a WinUI SwapChainPanel, with Windows 11-style chrome:
  seek bar, 10-second skips, volume, speed, fullscreen, auto-hiding controls, a
  **source** picker next to the **CC** and **audio track** buttons.
- Keyboard: **Space** play/pause, **Left/Right** seek ±10 seconds, **F**
  fullscreen, **M** mute, **E** opens the episode list, **I** toggles the title
  details card, **Esc** exits fullscreen or closes the
  source/subtitle/episode menus, **[** and **]** shift subtitle timing, Enter
  activates a focused button. Arrow keys adjust volume when the volume slider
  itself has focus.
- **Pause shows the details.** After a short pause, the title, year, runtime,
  rating, genres and plot (the episode's own synopsis for a series) fade in over
  a soft corner shade at the top-left: borderless, dense under the text and gone
  by its far edge, so it stays legible on a bright scene without reading as a
  panel. It disappears on resume, and **Info** in the top bar or **I** shows or
  hides it at any time while playing.
- The source picker switches sources while keeping the current playback
  position.
- The source picker is a menu in the transport bar, next to **CC**, audio and
  speed. The button reads the quality that is playing (**1080p**), or
  **Sources** before anything is chosen, and opens a flyout: resolution badge,
  release, size, format and addon per row, best source first, with a check on
  the one playing. The flyout light-dismisses, the video keeps playing behind
  it, and late addon answers fill it in while it is open. When autoplay is off,
  the picker opens over the title artwork so the title is still recognisable
  while you choose.
- Many addons means many streams, so the flyout filters by addon: **All** plus
  one tab per addon that answered, in your addon order, and the subheading
  switches to "24 of 120 streams" while a tab is selected. The tab strip scrolls
  sideways when the names don't fit, and hides with a single addon because
  **All** would just repeat it.
- **Episodes** — for a series, the **Episodes** button (or **E**) opens a season
  picker and episode list over the video. Rows show the thumbnail, episode tag,
  title, local watch progress, a **Now playing** marker and a watched check.
  Choosing another episode switches the same player in place, keeps fullscreen
  and volume, uses that episode's remembered source or the best available one,
  and resumes its saved position. No trip back to Details is needed.
- **Autoplay** — Settings → Playback has a single **Start playing
  automatically** switch, off by default. When on, choose a **Preferred
  quality**: Best available, 4K, 1440p, 1080p, 720p or 480p. Quality labels come
  from each addon's source metadata; sources that don't declare a resolution are
  shown as **Unknown**. If the preferred quality isn't available, Pirsa uses the
  best available source. With the switch off, every title opens the source
  picker.
- **Auto-advance** — Settings → Playback → **Play next episode automatically**
  (default on). The transition keeps using the fastest path: a fresh remembered
  source for the next episode starts instantly, otherwise the best found source
  is used even when the source picker is normally shown. Turning this off keeps
  the Up next card, so episodes can still be skipped by hand.
- **Video engine** — Settings → Playback → **Video engine** selects
  **Automatic**, **libVLC** or **mpv**. Automatic uses mpv when it is available
  and falls back to libVLC; picking libVLC explicitly overrides that. The
  choice is saved locally and applied the next time the player opens.
- Playback engine details, source discovery, caching and diagnostics are in
  [PLAYER.md](PLAYER.md).

## Subtitles

- The **CC** button in the player opens a two-step picker:
  1. **Off**, the preferred language (pinned and labelled **Preferred**), the
     tracks **embedded in this video**, then one row per addon language in
     alphabetical order. Languages are grouped across all subtitle addons. No
     language is hidden: the picker is where one title gets overridden, not
     where the preference is set. Embedded tracks apply instantly and need no
     download.
  2. For a language with multiple tracks, a version list opens. Every row shows
     the release group, file name, addon, frame rate and encoding, so you can
     pick the version that matches your release. The active version is shown
     above the list and remains selected when you reopen the picker.
- Languages with a single track apply immediately. **Off** disables subtitles
  immediately.
- **Subtitle timing** sits in the same panel: **Earlier** and **Later** shift
  the active track in 0.1-second steps, the value and a plain-language hint
  update live, and **Reset** appears only once you have changed it. Hold either
  button to sweep faster. **`[`** and **`]`** do the same while watching and
  show a short timing toast over the picture, so the panel does not need to be
  open. Timing works for embedded and addon tracks alike and is remembered per
  title, including across source switches and the next episode.
- **Subtitles move clear of the controls.** While the chrome is showing,
  subtitles ride up by the height of the control bar and settle back to the
  bottom of the picture when it hides. On a letterboxed film only the part of
  the bar that actually covers the picture counts, so the shift is never larger
  than it needs to be. Tracks that carry their own hard-coded positions (some
  embedded ASS) keep their own placement.
- **Preferred subtitle language** (Settings → Playback) turns the first matching
  track on automatically for every title and episode, including the next episode
  after auto-advance, and pins that language to the top of the picker. Picking
  another language in the player applies to that title only — a series keeps it
  for its other episodes — so one foreign film never changes the default. Picking
  the preferred language again clears that per-title choice, changing the
  preference clears all of them, and choosing **Off** is not remembered. Two-
  and three-letter codes (en / eng) count as the same language.
- The current subtitle and its timing stay selected across source switches; an
  embedded language is re-selected when the new source has a matching track.
- Subtitle files are downloaded with .NET first and handed to VLC as local
  files. This avoids a libvlc 3.0.24 GnuTLS crash that can occur when remote
  subtitle downloads overlap with stream TLS sessions.
- Adding another subtitle addon in Settings automatically adds its languages to
  the same picker.

## Addon links

- Clicking a `stremio://` addon link opens Pirsa and asks before anything is
  installed. The prompt reads the manifest and shows the addon's name, version,
  host and what it provides, then explains that addons are third-party.
- **Install** adds it; **Cancel** works even while the manifest is loading. An
  invalid link or a manifest that cannot be loaded changes nothing.
- A successful install opens Settings → Addons with a notice. While the player
  is open the outcome is shown in a dialog instead, so a running video is never
  closed silently.
- A link for an addon that is already configured opens Settings with that
  message instead of adding a duplicate.
- Addon links can also be pasted into Settings → Addons, including the
  `https://web.stremio.com/#/addons?addon=…` share form.
- Portable builds can turn Windows link handling on or off in Settings →
  Addons. Packaged builds register it through the app package, and the installer
  registers it per user.

## Sync

- **Settings → Sync** connects one or more watched-state providers. Each one is
  an independent mirror; a failure in one never blocks another.
- **Simkl** connects through its AUTH V2 device flow: Pirsa shows a short code,
  you approve it at `simkl.com/pin`, and watched history, Plan to Watch and
  paused playbacks start syncing. Your Simkl password is never shared, and the
  connection refreshes on its own.
- **Continue watching from other clients** — a pause saved by Plex, a
  media-server plugin or another Simkl client appears in Pirsa's Continue
  watching with its progress. Playback saved here is not pushed back yet.
  Simkl keeps paused sessions for 7 days on Free, 30 on PRO and 90 on VIP.
- **WebDAV** is available too: give Pirsa a folder URL, user name and app
  password (Nextcloud, ownCloud, Box or any other WebDAV host). Pirsa keeps one
  small `pirsa-sync.json` document in that folder.
- Watching a title, marking it watched or unwatched, and adding it to or
  removing it from the watchlist are pushed a few seconds later. A change made
  offline is pushed on the next launch, before that launch's pull is applied.
- Each entry carries the time it changed, so two devices agree on the newer
  fact. Removing a watched mark or a watchlist title is a timestamped fact too,
  so it cannot be resurrected by the other device.
- **Removing is not the same as never seeing it:** a local "clear history" or a
  trimmed row does not delete anything on a provider, while marking a title
  unwatched does, on every connected provider.
- The Sync settings page shows the last sync time and the last error per
  provider, with **Sync now** and **Disconnect**. Disconnecting deletes the
  saved credentials and leaves both local history and the remote account
  untouched.
- Credentials are encrypted for the current Windows user and are never written
  to `settings.json` or the player log.
- Only titles with an IMDb id are synced. Live TV and custom id prefixes are
  skipped because there is no portable key to match on.
- Sync is off until a provider is connected, and it never sends stream URLs,
  addon tokens or playback positions. See [SYNC.md](SYNC.md) for the design.

## Updates

- Every launch of an installer or portable build checks the project's latest
  GitHub Release in the background. A failed check is silent, so an offline
  launch is unchanged. The request carries no account, device or viewing data.
- When a newer version exists, a notice appears in the corner with the version
  and download size. **Download** fetches the release inside Pirsa; the notice
  shows progress and the app stays usable.
- The download is checked against the release's published SHA-256 before
  **Install** appears. A corrupted or tampered download is discarded.
- **Install** closes Pirsa and hands off:
  - An installed copy runs the per-user installer silently, then reopens the
    new version.
  - A portable copy replaces its own folder from the release ZIP, then reopens.
    It falls back to a clear message when the folder is not writable.
- Packaged builds (Store, or a development run through `winapp`) do not
  self-update; updates belong to the package owner.

## Limitations

- Auto-advance stops at the end of the last known episode and does not know
  about unaired episodes.
- Sync supports Simkl and WebDAV. MDBList, Trakt and AniList adapters are
  planned; nothing else leaves the machine until you connect a provider.
- Simkl anime catalogs are not synced yet: AniDB/absolute episode numbering does
  not map cleanly onto Stremio/Cinemeta seasons. Simkl remote un-watches and
  removals are not detected by a delta.
- Resume positions are pulled from Simkl but not pushed to it yet; the push half
  is the scrobble lifecycle.
- Sync covers watched state and the watchlist only. Resume positions,
  remembered sources and addons stay on this device.
- A `stremio://` link opened while Pirsa is already running starts a second
  window instead of handing the link to the running one.
- Torrent `infoHash` streams and `externalUrl`-only entries are skipped; only
  direct HTTP(S) sources are played.
