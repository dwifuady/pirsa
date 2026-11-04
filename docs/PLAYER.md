# Player

Technical notes on playback, source discovery and caching. For the user-facing
controls see [FEATURES.md](FEATURES.md).

## Playback sources

- Every enabled addon that declares a `stream` resource is queried in your addon
  order, but the picker fills in as answers arrive: one slow addon never delays
  the sources others already returned.
- Duplicate URLs keep the entry from the higher-priority addon.
- Pressing Play can start the first source that arrives without waiting for the
  rest. By default, pressing Play opens the picker as soon as something is
  available; choose a source to start.
- Sources are deduplicated by URL and ranked 1080p HLS first.
- Headers from `behaviorHints.proxyHeaders` are forwarded to VLC as media
  options when needed.
- Source discovery queries every configured addon that declares a `stream`
  resource for the media type and id prefix.
- Discovery can take 10–20 seconds depending on the addon/provider; the loading
  screen stays up until sources arrive. Results are cached per movie/episode for
  10 minutes (empty results for 1 minute) and dropped when the addon list
  changes. The details page prefetches them, so Play normally opens the picker
  instantly.

## Engine

- **LibVLCSharp.WinUI** with native **libvlc 3.0.24**. VLC is used because these
  addon sources are commonly 4K HEVC/HDR MKV, HLS, or DASH; MediaPlayerElement
  would fail on HEVC without extra codec packages.
- The video surface is a WinUI `SwapChainPanel`, so the transport chrome is
  regular XAML (it stays native, themeable, and keyboard accessible).
- The native libvlc binaries add roughly 150–200 MB to the packaged app and are
  licensed LGPL-2.1; review your distribution obligations if you publish it.

## Local stream cache

An on-demand HTTP source plays through a small localhost server
(`StreamCacheSession`) instead of straight from the addon:

- Two download slots pull the file into a temporary file in the `cache` folder of
  the data root (see [ADDONS.md](ADDONS.md#where-data-lives)), up to 128 MB
  ahead of the furthest read, and the player reads ranges from disk.
- Seeks into the downloaded window become a disk read — measured at 150–450 ms
  against a source only 1.5× faster than real time, where the same seek takes
  5 s over the network.
- A seek outside the window redirects a slot and waits, exactly like playing
  direct.
- Live TV, HLS, and servers that ignore byte ranges skip the cache and play
  direct.
- Temp files are deleted when playback stops or the source changes, and
  leftovers older than six hours are removed on the next run.
- Only the newest connection that is waiting may redirect a downloader: VLC
  opens a fresh connection for every seek and can leave the previous one open,
  and without that rule two readers in different places send the same
  downloaders back and forth and neither ever gets its data.

## Seek cache

- VLC's own demuxer cache is set to **3 seconds** for on-demand titles and
  **2.5 seconds** for live TV (kept shorter so it stays near the live edge).
- It is a jitter buffer, not a seek window: VLC answers every seek with a fresh
  range request, and the buffering percentage is measured against this target,
  so a bigger value means a *longer* wait after each skip.
- The local stream cache above is what makes skips instant.

## Buffering overlay

The overlay follows the playhead, not VLC's buffering percentage. VLC reports
"Playing" over a frozen picture while it climbs from 0% to 100% over several
seconds, and keeps reporting low percentages after a seek that cost nothing.

- The overlay appears only after the position has stood still for 1.2 seconds,
  and disappears the moment frames move again.
- The last four seconds of a title never show a spinner: VLC stops advancing the
  clock while the final buffered frames drain, which looked like a stall at 99%.

## Diagnostics

`player.log` in the data root records:

- every seek and how long the playhead stayed frozen afterwards,
- the source format and options,
- VLC's own stream-layer messages.

It is reset when the player opens, capped at 1 MB, and never leaves the machine.
The header names the data root in use.

## Subtitles

Subtitle files are downloaded with .NET first and handed to VLC as local files.
This avoids a libvlc 3.0.24 GnuTLS crash that can occur when remote subtitle
downloads overlap with stream TLS sessions. Picker behavior is described in
[FEATURES.md](FEATURES.md#subtitles).
