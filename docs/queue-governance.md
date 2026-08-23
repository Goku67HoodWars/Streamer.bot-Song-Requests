# Queue governance (blocklists · limits · cooldowns · explicit filter)

Viewer-facing gates on song requests. All local, applied to **viewer** requests (channel-point redeems and
chat commands); the streamer's own **Add** box in the dock bypasses every gate. A blocked/limited
request fails with a chat reason and is auto-refunded, exactly like the existing failure paths.

Configure it live from the dock's **Rules** and **Blocklists** cards, or in `config.txt` / the blocklist files.

## Rules

| Setting | Config key | Default | Meaning |
|---|---|---|---|
| Max queue length | `MaxQueueLength` | `0` | max total outstanding requests across both lanes (`0` = unlimited) |
| Max per user | `MaxRequestsPerUser` | `0` | max outstanding requests one viewer can have queued (`0` = unlimited) |
| Cooldown | `SrCooldownSec` | `0` | seconds between *any* two requests globally (`0` = off) |
| Per-user cooldown | `SrPerUserCooldownSec` | `0` | seconds a viewer must wait between their own requests (`0` = off) |
| Block explicit tracks | `BlockExplicit` | `0` | refund Spotify tracks flagged `explicit` |

Cooldowns start only after a **successful** request (a failed/refunded attempt doesn't arm the timer).
Limits count only *outstanding* requests — once a song plays, is skipped, or is removed, it no longer counts.

## Blocklists

Three lists, **one entry per line**, stored next to the other data:

| List | File | Matches |
|---|---|---|
| Users | `%AppData%\SongRequests\blacklist_users.txt` | exact username (case-insensitive) |
| Songs | `%AppData%\SongRequests\blacklist_songs.txt` | a Spotify/YouTube link/URI/ID (exact), **or** a case-insensitive substring of `title - artist` |
| Artists | `%AppData%\SongRequests\blacklist_artists.txt` | case-insensitive substring of the artist / channel name |

Song and artist entries are **substring** matches, so a short term can block a lot — e.g. an artist entry
`rick` also blocks "Rick Astley" and "Rickie Lee Jones". Use full names/links to be precise.

From the dock **Blocklists** card you can add/remove entries inline, and **Block current** adds the
now-playing song (by title) or artist with one click.

## Rejection reasons (chat + refund)

Each gate returns a status that drives the chat reply (when announcements are on) and the auto-refund:
`user blocked` · `cooldown` · `queue full` · `user limit` · `song blocked` · `artist blocked` · `explicit`.

## Notes / limits

- **Per-user counting is by request, not user level.** Per-*tier* limits (sub / VIP / mod get more) aren't
  wired yet — the native Twitch bot knows a viewer's level for command permissions, but the request-limit
  gate counts every viewer equally.
- Explicit filtering is Spotify-only (YouTube exposes no explicit flag; age-restricted videos are already rejected).
