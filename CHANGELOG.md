# Changelog

## v1.1.2 — 2026-09-24

Reliability patch from a full pipeline audit.

- **Skip always advances** — if a YouTube item can't start right now (player source not loaded, still downloading, or the lane is off), Skip now skips the Spotify track instead of silently doing nothing.
- **Dock controls survive an engine restart** — the dock re-fetches its token on a 403, so Skip/Pause/Add/etc. no longer silently no-op for ~1.5s after a restart.
- **YouTube audio survives an engine restart** — the audio stream is no longer token-gated, so a playing song isn't cut off when the engine updates/restarts.
- **No more dead air after a YouTube item** — Spotify resume now finds and reactivates a device (transfers playback) instead of failing silently when Spotify Connect dropped the paused device.
- **Spotify self-heals mid-session** — a revoked/expired token flips the connection off so it re-authenticates on the next request without an engine restart.
- **Auto-update hardening** — the launcher no longer double-extracts when two launches race, and defers to a combined app+runtime update on a runtime bump.
- **OBS audio repair** also enables "Control audio via OBS" on a matched player source, not just monitoring.

## v1.1.1 — 2026-09-24

- **OBS audio fix** — the auto-start script now sets the "YouTube Player" source to **Monitor and Output** (and repairs an existing source still set to "monitor off"), so the streamer hears YouTube too. Fixes "YouTube plays but I can't hear it."
- **Spotify self-heal** — the engine re-reads your keys/token and retries authentication when a request arrives while disconnected, so reconnecting Spotify in the app takes effect **without an engine restart**.
- **Docs** — corrected the OBS audio-monitoring step in the README (it wrongly said to leave monitoring off).

## v1.1.0 — 2026-08-23

All additions are **optional**; the existing Streamer.bot flow is unchanged.

- **OBS now-playing files** — writes `nowplaying.txt` + `cover.png` (plus split `artist` / `title` / `requester`) for OBS Text/Image sources, with custom format tokens and pause behavior. No browser source required.
- **Queue governance** — user / song / artist blocklists, max-queue + per-user limits, global + per-user cooldowns, and an explicit-track filter, all live from the dock.
- **Native Twitch chat bot** (IRC + app OAuth) — chat commands (`!sr`, `!song`, `!queue`, `!pos`, `!skip`, `!voteskip`, `!vol`, `!bansong`, …) with per-command user levels and cooldowns. In app mode it can also run channel-point rewards + bits natively, with no Streamer.bot required.

## v1.0.1 — 2026-07-03

- Dock: plain numeric max-length input (no spinner arrows).

## v1.0.0 — 2026-07-03

- Initial release: Twitch channel-point song requests for Spotify and YouTube, played through OBS. Control dock, now-playing overlay, auto-refunds, and tiny auto-updates.
