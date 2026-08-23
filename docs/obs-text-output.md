# OBS now-playing files (text + cover art)

The engine writes the current song to plain files that OBS **Text** and **Image** sources can read
directly — the classic local "now playing" overlay, with **no browser source required**. This runs fully
locally for both the Spotify and YouTube lanes; nothing is uploaded anywhere.

## Files

Written to the output folder (default `%AppData%\SongRequests\obs`):

| File | Contents |
|---|---|
| `nowplaying.txt` | the formatted now-playing line (see **Format tokens**) |
| `artist.txt` | artist only *(when `SplitOutput=1`)* |
| `title.txt` | title only *(when `SplitOutput=1`)* |
| `requester.txt` | `RequesterPrefix` + requester name, or empty for autoplay *(when `SplitOutput=1`)* |
| `cover.png` | album / video thumbnail art *(when `DownloadCover=1`)* |

## Add it in OBS

1. **Sources → + → Text (GDI+)** → tick **Read from file** → browse to `nowplaying.txt`.
2. *(optional art)* **Sources → + → Image** → browse to `cover.png`.
3. *(optional split)* set `SplitOutput=1` and point separate Text sources at `artist.txt` / `title.txt` / `requester.txt`.

The dock has an **OBS now-playing files** toggle to turn the whole thing on/off live.

## Format tokens

Used in `OutputFormat` (and available for future templated outputs):

| Token | Value |
|---|---|
| `{artist}` / `{single_artist}` | artist name |
| `{title}` | track title |
| `{req}` / `{requester}` | the viewer who requested it (empty for Spotify autoplay) |
| `{url}` / `{uri}` | link to the track (Spotify track URL, or `youtu.be/…`) |
| `{{ … }}` | **conditional block** — kept only when the song was actually requested by someone |

**Example:** `OutputFormat={artist} - {title}{{  ·  requested by {req}}}`
→ *requested song:* `Daft Punk - One More Time  ·  requested by bob`
→ *autoplay song:* `Daft Punk - One More Time`

## Config keys (`%AppData%\SongRequests\config.txt`)

| Key | Default | Notes |
|---|---|---|
| `OutputEnabled` | `1` | master on/off (also the dock toggle) |
| `OutputDir` | *(empty → `%AppData%\SongRequests\obs`)* | folder the files are written to |
| `OutputFormat` | `{artist} - {title}` | `nowplaying.txt` template |
| `SplitOutput` | `0` | also write `artist.txt` / `title.txt` / `requester.txt` |
| `RequesterPrefix` | `Requested by ` | prepended in `requester.txt` (config values are trimmed, so a trailing space set here is dropped) |
| `DownloadCover` | `1` | save art to `cover.png` (a 1×1 transparent PNG is written when idle-cleared) |
| `AppendSpaces` | `0` | pad lines with trailing spaces for marquee scrolling |
| `SpaceCount` | `10` | number of trailing spaces when `AppendSpaces=1` |
| `PauseBehavior` | `nothing` | while paused/idle: `nothing` (keep last song) · `clear` (blank the files) · `text` (show `CustomPauseText`) |
| `CustomPauseText` | *(empty)* | shown when `PauseBehavior=text` |

Changes to `config.txt` are picked up on the next engine start/restart.

**Or edit it live from the dock:** the **OBS output** card has the format string, split/cover toggles, and
pause behavior — changes apply on the engine's next now-playing tick, no restart needed.
