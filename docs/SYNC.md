# Sync

Technical notes on syncing watched state and the watchlist between devices.
For the user-facing controls see [FEATURES.md](FEATURES.md#sync).

## What syncs

- **Watched state** — every movie and episode marked watched, with the time it
  was marked, so a merge can decide which side is newer.
- **Watchlist** — the saved titles and when they were added.
- **Unwatched / removed** — a timestamped tombstone, so the next pull cannot
  resurrect something you removed on another device.
- **Resume positions** — with Simkl: pulled from paused playbacks, and pushed
  from the player's start, pause, stop and completion events.

Resume positions sync with Simkl both ways: a pause saved by another client
shows up in Continue watching, and playback here is reported through the
scrobble lifecycle so another client can resume it.

It does **not** sync:

- Remembered stream sources. They are tied to a provider and a device.
- Live TV and custom id prefixes that have no IMDb id. Both are skipped because
  there is no portable key to match on.

## Providers

Several providers can be connected at once. Each one is an independent mirror;
a failure in one never blocks another, and disconnecting one leaves local data
alone.

| Provider | Status | Auth | Notes |
| --- | --- | --- | --- |
| WebDAV / Nextcloud | Implemented | Folder URL + user + app password | One `pirsa-sync.json` file; the only provider that needs no third-party account |
| Simkl | Implemented | AUTH V2 device flow | Official free API; watched history, Plan to Watch and paused playbacks |
| MDBList | Planned | API key | Aggregator; verify write support and propagation before treating it as a history sink |
| Trakt | Planned | OAuth2 | Kept for the installed base; no design dependency on it |
| AniList | Planned | OAuth2 GraphQL | Anime only; absolute episode numbering |

The interface (`ISyncProvider`, `ISyncSession`) keeps auth and transport out of
the engine, so adding a provider is one file under `Services/Sync/Providers`.

## Simkl

- **Auth** is Simkl AUTH V2's device flow: Pirsa asks for an 8-character code,
  opens `simkl.com/pin`, and polls until the user approves. No password and no
  client secret are involved. AUTH V1 (the old 5-character PIN) is deliberately
  not used; Simkl is retiring it around April 2027.
- **Scopes:** `media:read media:write`. Without the write scope every push would
  fail with `403 insufficient_scope`.
- **Tokens:** the access token lasts 7 days and is refreshed when it is less
  than a day from expiry, or once after a `401`. The refresh token lasts 180
  days and slides forward on use. Refreshed tokens replace the encrypted
  credential file; the user does not re-authorize.
- **Client id:** client ids are public and safe to commit. Pirsa ships the
  project registration in `SimklSyncProvider.BuiltInClientId`, so users never
  see or paste one. A fork without its own registration can clear the constant,
  and Settings will ask for a client id from `simkl.com/settings/developer`.
- **Pull** follows Simkl's two-phase model: `/sync/activities` is read first,
  and `/sync/all-items` is only called when the top-level `all` timestamp moved.
  The first sync pulls the full library; later syncs send `date_from` and read
  only the delta. Each pull asks for `extended=full` and
  `episode_watched_at=yes` so episode rows carry their real dates.
- **Push** sends only facts newer than the last successful push, tracked in the
  provider cursor, plus batched removals. Writes use `/sync/history`,
  `/sync/add-to-list` (`to: plantowatch`) and `/sync/history/remove`.
- **Mapping:** a Simkl `plantowatch` item becomes a local watchlist entry.
  `watching`, `hold`, `dropped` and `completed` are watched-state inputs, not
  watchlist entries.
- **Removing from the local watchlist** removes the item from Simkl only when it
  has no watch history locally. A watched title stays in the Simkl library, so a
  local tidy-up cannot erase a watch event elsewhere.
- **Continue watching** is pulled from `/sync/playback`, gated on the
  `playback` timestamps in `/sync/activities`, so a pause saved by Plex, a
  media-server plugin or another Simkl client becomes a normal local resume
  row. The item's percentage is turned into a position with the runtime from
  the Simkl detail endpoint; an existing local duration wins because it was
  measured against the file that actually plays. A watched title is left
  watched — a remote pause never clears the mark.
- **Removing a resume is real.** Dismissing a title from Continue watching, or
  marking it watched, deletes the matching Simkl playback with
  `DELETE /sync/playback/{id}`. The id comes from the last pull or from the
  pause response, so the delete works even in the same session that created it.
  A 404 means it was already gone.
- **Playback retention is per plan:** Simkl keeps paused sessions for 7 days on
  Free, 30 on PRO and 90 on VIP. An old pause simply stops being returned.
- **Push uses scrobble, not history.** The player reports four moments:
  - `Started` when playback begins or resumes → `POST /scrobble/start`
  - `Paused` → `POST /scrobble/pause`
  - `Stopped` when leaving the player → `POST /scrobble/stop`
  - `Completed` at the end → `POST /scrobble/stop` with 100%

  A stop at or above 80% marks the item watched; below it saves a resume point.
  This is separate from manual "mark watched", which still uses
  `/sync/history`; the two duplicate each other safely.
- **The 20-second lock is respected.** Simkl rejects any second scrobble write
  for a user within 20 seconds with `400 RATE_LIMIT`. The engine serialises
  scrobbles and retries once after 21 seconds, so a pause followed by leaving
  the player still lands. A `409` on stop means the watch was already recorded
  within the hour and is treated as success. The player never waits for any of
  this; a failing scrobbler is silent and never interrupts playback.
- **Not synced yet:** anime catalogs (Simkl numbers anime by AniDB/absolute
  episodes, which does not map cleanly onto Stremio/Cinemeta season numbers) and
  ratings. Remote un-watches and remote removals are not detected by a delta;
  Simkl only reports those as a timestamp, not as rows.

## Merge rules

`SyncMerge.Plan(remote, local)` is pure and has no I/O. Resume rows are applied
separately by the engine through `WatchProgressStore.ApplyRemoteResumeAsync`,
which performs the same newest-wins check against the local position.

- The newer timestamp wins, per item key:
  - movie → `imdbId`
  - episode → `imdbId:season:episode`
  - watchlist item → `imdbId`
- A tombstone (`unwatched` / `removed`) participates in the same comparison as a
  watched mark. Removing an item is a fact with a time, not a missing row.
- Only the newer side is applied. Nothing is ever deleted because it is absent
  from the other side; absence is not a signal.
- Applying a remote watched mark never touches resume fields on an existing row.
- A remote resume is applied only when its `paused_at` is newer than the local
  position, and never over a watched mark.
- Applying a remote watched mark for a title with no local row creates a
  watched-only row (no position), which is what an explicit "mark as watched"
  already writes.

`SyncEngine` runs **pull → merge → push** for every provider, one at a time.
What push sends is the provider's choice: WebDAV rewrites the whole document,
while Simkl sends only facts newer than its cursor. Both read the full local
snapshot, so a missed event cannot leave the two sides permanently out of step.

## Storage

All of it lives in the data root described in
[ADDONS.md](ADDONS.md#where-data-lives):

| File | Contents |
| --- | --- |
| `sync.json` | Connections, account labels, last sync time, last error, tombstones |
| `sync-credentials.dat` | Per-provider secrets, DPAPI-encrypted for the current user |
| `pirsa-sync.json` (remote) | The WebDAV document: watched and watchlist entries with timestamps |

Credentials are never written to `settings.json` and never logged. Disconnect
deletes that provider's credentials and connection, and leaves local history
untouched.

## WebDAV document

```json
{
  "version": 1,
  "updatedAt": "2025-06-01T18:20:00+00:00",
  "watched": [
    {
      "imdbId": "tt0111161",
      "kind": "movie",
      "change": "watched",
      "at": "2025-05-30T20:10:00+00:00",
      "name": "The Shawshank Redemption"
    },
    {
      "imdbId": "tt0944947",
      "kind": "series",
      "season": 1,
      "episode": 1,
      "change": "watched",
      "at": "2025-05-31T21:00:00+00:00",
      "name": "Game of Thrones"
    }
  ],
  "watchlist": [
    {
      "imdbId": "tt1375666",
      "kind": "movie",
      "added": true,
      "at": "2025-05-29T12:00:00+00:00",
      "name": "Inception"
    }
  ]
}
```

Timestamps are UTC ISO-8601. The document is human-readable and small: a
thousand watched rows are well under a megabyte.

The WebDAV writer does not use `If-Match`, so two devices saving at the same
instant keep the later file. A pull merges both sides before the next push, so
the only cost is one redundant round trip, not a lost fact.

## Failure behaviour

- A failed sync is silent in the player and on Home. The provider row in
  Settings shows the last error and a **Sync now** button.
- A pull that fails never blocks a push to another provider.
- The engine debounces store changes for a few seconds, so a bulk mark or a
  playback finish produces one round trip, not one per row.
- Applying a remote change suppresses the store events it causes, so a pull
  cannot echo straight back out as a push.

## Security

- Tokens and passwords are encrypted with DPAPI (`CurrentUser`), so another
  Windows account cannot read them and a copied data folder does not carry a
  usable secret.
- The WebDAV password should be an app password, not the account password.
- Sync is off until a provider is connected. Each document contains IMDb ids,
  timestamps and titles — no stream URLs, addon tokens or viewing positions.

## Adding a provider

1. Add a class implementing `ISyncProvider` and `ISyncSession` under
   `Services/Sync/Providers`.
2. Describe it with `SyncProviderDescriptor` and add it to `SyncProviders.All`.
3. Map its auth to `SyncCredentials`. Device-flow providers report the code
   through the `IProgress<SyncAuthPrompt>` passed to `ConnectAsync`; the
   Settings page renders the rest from the descriptor.
4. Keep provider state in `ISyncSession.Cursor` (the engine persists it) and
   expose refreshed tokens through `UpdatedCredentials` (the engine rewrites the
   encrypted credential file). `WebDAV` has no cursor; Simkl stores its last
   activities snapshot there.
5. Map `WatchEvent` / `WatchlistChange` to the provider's payload and back.
6. Leave anything the provider cannot express (for example, absolute anime
   numbering or live TV) out of the wire model rather than inventing a lossy
   mapping.
