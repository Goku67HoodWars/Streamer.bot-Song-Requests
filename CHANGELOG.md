# Changelog

## v1.1.0 — 2026-08-23

All additions are **optional**; the existing Streamer.bot flow is unchanged.

- **OBS now-playing files** — writes `nowplaying.txt` + `cover.png` (plus split `artist` / `title` / `requester`) for OBS Text/Image sources, with custom format tokens and pause behavior. No browser source required.
- **Queue governance** — user / song / artist blocklists, max-queue + per-user limits, global + per-user cooldowns, and an explicit-track filter, all live from the dock.
- **Native Twitch chat bot** (IRC + app OAuth) — chat commands (`!sr`, `!song`, `!queue`, `!pos`, `!skip`, `!voteskip`, `!vol`, `!bansong`, …) with per-command user levels and cooldowns. In app mode it can also run channel-point rewards + bits natively, with no Streamer.bot required.

## v1.0.1 — 2026-07-03

- Dock: plain numeric max-length input (no spinner arrows).

## v1.0.0 — 2026-07-03

- Initial release: Twitch channel-point song requests for Spotify and YouTube, played through OBS. Control dock, now-playing overlay, auto-refunds, and tiny auto-updates.
