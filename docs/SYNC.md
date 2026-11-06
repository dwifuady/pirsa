# Sync

Technical notes on syncing watched state and the watchlist between devices.
For the user-facing controls see [FEATURES.md](FEATURES.md#sync).

## What syncs

- **Watched state** — every movie and episode marked watched, with the time it
  was marked, so a merge can decide which side is newer.
- **Watchlist** — the saved titles and when they were added.
- **Unwatched / removed** — a timestamped tombstone, so the next pull cannot
  resurrect something you removed on another device.

It does **not** sync:

- Resume positions. Providers do not store them, and merging two positions
  would rewind playback. Positions stay on the device that played them.
- Remembered stream sources. Same reason.
- Live TV and custom id prefixes that have no IMDb id. Both are skipped because
  there is no portable key to match on.

## Providers

Several providers can be connected at once. Each one is an independent mirror;
a failure in one never blocks another, and disconnecting one leaves local data
alone.

| Provider | Status | Auth | Notes |
| --- | --- | --- | --- |
| WebDAV / Nextcloud | Implemented | Folder URL + user + app password | One `pirsa-sync.json` file; the only provider that needs no third-party account |
| Simkl | Planned | OAuth2 device flow | Official free API; watched history, watchlist, scrobble |
| MDBList | Planned | API key | Aggregator; verify write support and propagation before treating it as a history sink |
| Trakt | Planned | OAuth2 | Kept for the installed base; no design dependency on it |
| AniList | Planned | OAuth2 GraphQL | Anime only; absolute episode numbering |

The interface (`ISyncProvider`, `ISyncSession`) keeps auth and transport out of
the engine, so adding a provider is one file under `Services/Sync/Providers`.

## Merge rules

`SyncMerge.Plan(remote, local)` is pure and has no I/O.

- The newer timestamp wins, per item key:
  - movie → `imdbId`
  - episode → `imdbId:season:episode`
  - watchlist item → `imdbId`
- A tombstone (`unwatched` / `removed`) participates in the same comparison as a
  watched mark. Removing an item is a fact with a time, not a missing row.
- Only the newer side is applied. Nothing is ever deleted because it is absent
  from the other side; absence is not a signal.
- Applying a remote watched mark never touches resume fields on an existing row.
- Applying a remote watched mark for a title with no local row creates a
  watched-only row (no position), which is what an explicit "mark as watched"
  already writes.

`SyncEngine` runs **pull → merge → push** for every provider, one at a time.
Push sends the full local snapshot so a missed event can never leave the two
sides permanently out of step.

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
3. Map its auth to `SyncCredentials`; the Settings page renders the form from
   the descriptor.
4. Map `WatchEvent` / `WatchlistChange` to the provider's payload and back.
5. Leave anything the provider cannot express (for example, absolute anime
   numbering or live TV) out of the wire model rather than inventing a lossy
   mapping.
