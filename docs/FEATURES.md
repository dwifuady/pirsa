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
- History is per device; there is no sync between machines.

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

- LibVLCSharp + libvlc in a WinUI SwapChainPanel, with Windows 11-style chrome:
  seek bar, 10-second skips, volume, speed, fullscreen, auto-hiding controls, a
  source picker, and a **CC subtitle picker**.
- Keyboard: **Space** play/pause, **Left/Right** seek ±10 seconds, **F**
  fullscreen, **M** mute, **E** opens the episode list, **Esc** exits fullscreen or
  closes the source/subtitle/episode panels, **[** and **]** shift subtitle
  timing, Enter activates a focused button. Arrow keys adjust volume when the
  volume slider itself has focus.
- The source picker switches sources while keeping the current playback
  position.
- Sources are grouped into horizontal tabs: **All** plus one tab per addon that
  answered, in your addon order. A long addon list stays a short scroll instead
  of one endless list, and the tab strip scrolls sideways when the names don't
  fit. With a single addon the tabs are hidden because they would just repeat
  **All**.
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
- Playback engine details, source discovery, caching and diagnostics are in
  [PLAYER.md](PLAYER.md).

## Subtitles

- The **CC** button in the player opens a two-step picker:
  1. **Off**, the tracks **embedded in this video**, then one row per addon
     language. Languages are grouped across all subtitle addons and sorted by
     how many tracks are available. Embedded tracks apply instantly and need
     no download.
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
- **Preferred subtitle language** (Settings → Playback) turns the first matching
  track on automatically for every title and episode, including the next episode
  after auto-advance. Picking a language in the player remembers it as the
  preference, while choosing **Off** there only applies to the title you are
  watching. Two- and three-letter codes (en / eng) count as the same language.
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

## Limitations

- Auto-advance stops at the end of the last known episode and does not know
  about unaired episodes.
- Watch history is per device; there is no sync between machines.
- Addon order is global: per-type or per-catalog priorities are not supported
  yet.
- A `stremio://` link opened while Pirsa is already running starts a second
  window instead of handing the link to the running one.
- Torrent `infoHash` streams and `externalUrl`-only entries are skipped; only
  direct HTTP(S) sources are played.
