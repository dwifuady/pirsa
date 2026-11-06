# Player

Technical notes on playback, source discovery and caching. For the user-facing
controls see [FEATURES.md](FEATURES.md).

## Playback sources

- Every enabled addon that declares a `stream` resource is queried in your addon
  order, but the picker fills in as answers arrive: one slow addon never delays
  the sources others already returned.
- The picker is the source button in the transport bar: it shows the quality
  that is playing (**1080p**) and opens a light-dismiss flyout. Rows are ranked
  by addon order, then resolution; each row shows the resolution badge, the
  release/provider, size, format and addon. Duplicate URLs are dropped before
  ranking, so a source appears under the tab of the addon that won it.
- The tabs are **All** plus one per addon that returned a source, in addon
  order; the selected tab survives a rebuild as late answers arrive, and the
  list scrolls back to the top when you switch.
- Duplicate URLs keep the entry from the higher-priority addon.
- Pressing Play can start the first source that arrives without waiting for the
  rest. By default, pressing Play opens the picker as soon as something is
  available; choose a source to start. The flyout opens over the title artwork,
  not a blank video surface, so the title stays recognisable while you choose.
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
- Playback goes through `IPlaybackEngine`; libVLC is the only registered
  backend today. Another engine implements that interface and registers a
  factory, without touching source discovery, resume or the chrome.
- The video surface is a WinUI `SwapChainPanel`, so the transport chrome is
  regular XAML (it stays native, themeable, and keyboard accessible).
- The native libvlc binaries add roughly 150–200 MB to the packaged app and are
  licensed LGPL-2.1; review your distribution obligations if you publish it.
- Leaving the player releases VLC before the frame navigates. `Stop()` joins
  libvlc's input and output threads, and a source stalled in a socket read can
  hold that for a second or more, so it runs on a worker thread while the page
  (and its swap chain) stays up — the frame only leaves once VLC has released
  the surface, with a 2-second safety timeout. The local stream cache closes
  VLC's connections first, so that pending read ends immediately.

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

## Play and pause

- The transport does not trust `MediaPlayer.IsPlaying`: its answer trails every
  command, and `Pause()` is a toggle. Both would let a press land on a stale
  state and flip the wrong way, and a press while the player is still opening
  would be swallowed. So a press records an *intent*, pauses with the explicit
  `SetPause(true)`, and the button shows the intent until libvlc reports
  `Playing`/`Paused`.
- A press libvlc never answers is given up after 2 seconds: the button returns
  to libvlc's own state and the log says so. That is the difference between a
  slow pause and a lost one.
- Measured on 3.0.24, the press itself is not the delay: the log shows the
  press reaching libvlc and the `Paused` event coming back in **0-2 ms**. What
  takes 0.2-0.8 s is the audio: `wasapi` reports a 96000-frame device ring (two
  seconds at 48 kHz), libvlc queues decoded audio ahead of the clock, and the
  WASAPI session stays active for that long after the pause.
- libvlc's own mute cannot cut that remainder. Its mute is applied by the audio
  output thread — the thread busy playing the audio — and the log shows it
  landing **315-618 ms late** (its unmute lands in 1-2 ms, when that thread is
  running again). So the pause mute goes at the session directly
  (`Services/AudioSessionMute.cs`, WASAPI `ISimpleAudioVolume::SetMute`, ~2 ms),
  where the mixer applies it to audio that is already queued. The mute is lifted
  on resume, where libvlc's own inserted silence covers it.
- Resuming does not play that queued audio back, it pays for it: the log showed
  **228-813 ms of silence inserted after every resume**, equal to the queue depth
  at pause. libvlc 3's only public flush is a seek, so a resume seeks straight
  back to the frozen position — that flush clears the debt, so the sound starts
  with the picture. The seek must happen **after** `Play()`, never while paused:
  a seek issued during the pause leaves the demuxer reading while paused, which
  VLC logs as `prefetch: reading while paused (buggy demux?)` and which froze the
  playhead for 8 s while the buffer reported 100%. The seek is skipped whenever
  the source is not playing through the local read-ahead cache: live TV, HLS,
  direct HTTP and any source whose cache setup failed would all fetch for longer
  than the silence it saves. It is skipped in the last seconds of a title too.
  A first-play resume seek (`_resumeMs`) flushes the output on its own, so the
  resync stands down and leaves it to that.
- The whole pause/resume asymmetry is structural in libvlc 3: the aout core
  converts the audio queued at pause into a debt and repays it as silence, and
  its audio output modules never report that they emptied themselves. VLC 4's
  clock rewrite removes it, which is the only real fix.
- A session the user muted themselves is never touched: the page only takes over
  an unmuted session, and pressing **M** while paused hands control back.
- While paused, the D3D11VA decoder keeps trying to decode and logs
  `hardware acceleration picture allocation failed` about once a second: the
  vout holds every picture in its pool because nothing is being displayed. It is
  noise rather than breakage, but it is why a long pause is not free.
- The audio output chain is named (`--aout=mmdevice,directsound`) rather than
  left to VLC's scoring, so which module is in use is deterministic and shows up
  in the log. It is the same order VLC picks on its own (mmdevice 150,
  directsound 100, waveout 50), so this only removes the guesswork.

## Diagnostics

`player.log` in the data root records:

- every seek and how long the playhead stayed frozen afterwards,
- every play/pause press next to how long libvlc took to report the change, and
  a timeout line when it never did,
- the elapsed time of the pause mute, and whether it went through the session or
  fell back to libvlc's (late) mute,
- which audio output module VLC created (`using audio output module …`),
- the source format and options,
- VLC's own stream-layer messages,
- how long the video engine took to stop and be released when the player closed.

It is reset when the player opens, capped at 1 MB, and never leaves the machine.
The header names the data root in use.

Note that `--verbose=2` sends every libvlc debug message through managed code and
`PlayerLog.Write` appends synchronously, so the log fills its 1 MB cap quickly on
HLS sources. That traffic runs on VLC's own threads; treat a reproducible control
latency measured with the log on as an upper bound.

## Episode switching

- The player resolves full series metadata once for the **Episodes** list and
  for Up next. Details requests already carry it; a Continue watching request
  only carries a stub, so the player fetches it in the background.
- Choosing an episode saves the current position, drops the per-episode state
  (source list, stream cache, audio/subtitle tracks, Up next dismissal) and
  starts the new episode through the same path as auto-advance. A remembered
  source for that episode is used first, otherwise the best found source.
- The target episode's local history position is restored, so jumping into a
  half-watched episode resumes instead of restarting it.
- The list follows the series metadata: season 0 is shown as **Specials** and
  ordered last, matching the details page.

## Subtitles

Subtitle files are downloaded with .NET first and handed to VLC as local files.
This avoids a libvlc 3.0.24 GnuTLS crash that can occur when remote subtitle
downloads overlap with stream TLS sessions. Picker behavior is described in
[FEATURES.md](FEATURES.md#subtitles).

The lift is VLC's `sub-margin` vout variable, set to the control bar's footprint
in swap-chain pixels: bar height minus letterbox, plus a 12 DIP gap. The page
computes that geometry and hands the device-pixel margin to the backend through
`IPlaybackEngine.SetSubtitleLift`; the libVLC adapter applies it in
`Playback/LibVlc/SubtitleLift.cs`. libvlc 3 exposes no public setter for it, so
the adapter reaches the vout through exported entry points
(`libvlc_get_input_thread`, `input_Control(INPUT_GET_VOUTS)`, `var_SetChecked`);
the variable's own callback repositions the current subtitles without restarting
the stream. It is recomputed whenever the chrome toggles, a source starts, or the
surface resizes, and it disables itself and logs once if a future libvlc stops
exporting those functions — re-check it when bumping
`VideoLAN.LibVLC.Windows` off 3.0.24.
