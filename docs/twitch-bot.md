# Native Twitch chat bot (commands)

A direct Twitch chat connection that lets viewers drive requests with **chat commands** (`!sr`, `!song`,
`!queue`, …) — something the Streamer.bot-only flow never had. It runs **alongside** Streamer.bot:
channel-point redemptions, refunds, and reward pausing keep going through SB exactly as before; this only
adds the chat side.

Two connection modes, pick one with **`TwitchAuthMode`**:

| Mode | `TwitchAuthMode` | Setup | Auth longevity | Notes |
|---|---|---|---|---|
| **IRC** | `irc` (default) | paste a chat OAuth token | token **expires** (no refresh) | fastest to set up; chat only |
| **App** | `app` | create a Twitch dev app + one-time OAuth | **auto-refreshes** (never re-paste) | future-proof; unlocks native rewards/bits/polls later |

Both modes feed the exact same command engine (same commands, levels, cooldowns, responses).

## Setup — IRC mode

1. Pick the account the bot posts as (your channel account, or a dedicated bot account).
2. Get a **chat OAuth token** for it with scopes **`chat:read`** + **`chat:edit`** (`oauth:…`).
3. In `%AppData%\SongRequests\config.txt`:
   ```
   TwitchBotEnabled=1
   TwitchAuthMode=irc
   TwitchBotUsername=your_bot_account
   TwitchBotToken=oauth:xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
   TwitchChannel=your_channel
   ```
4. Restart the engine → log shows `Twitch bot: connected to #your_channel.`

> ⚠️ A chat token isn't refreshed and will eventually expire — you'll have to regenerate it. Use **app mode** to avoid that.

## Setup — app mode (OAuth dev app)

1. Create an app at [dev.twitch.tv/console/apps](https://dev.twitch.tv/console/apps). Set the **OAuth Redirect URL** to exactly
   `http://localhost:3737/twitchcallback` (or your own `TwitchRedirectPort`). Copy the **Client ID** and generate a **Client Secret**.
2. In `config.txt`:
   ```
   TwitchBotEnabled=1
   TwitchAuthMode=app
   TwitchClientId=your_client_id
   TwitchClientSecret=your_client_secret
   ```
3. Restart the engine. On first run it **opens your browser** to authorize (or use **Connect Twitch** from the dock:
   `cmd=twitchauth`). Approve, and it saves a refresh token to `twitch_token.txt` and reconnects. The bot **posts and reads
   as the account you authorize with** (broadcaster = sender).

Scopes requested: `user:read:chat user:write:chat user:bot channel:bot channel:manage:redemptions channel:read:redemptions bits:read`.

## Native rewards & bits (app mode only)

App mode can own the whole channel-points path, so **Streamer.bot isn't required** for requests:

- **Manage the reward** — set `ManageReward=1` (and `RewardCost`); on start the engine reuses the reward named
  `RewardName` if it exists, or **creates** it (user-input on). Needs `channel:manage:redemptions` and an Affiliate/Partner channel.
- **Handle redemptions natively** — set `RedemptionSource=twitch`. The engine subscribes to redemptions via EventSub,
  queues them, then **fulfills** on success or **refunds** (`RefundFailed=1`) on failure — via Helix, no SB. (When
  `RedemptionSource=twitch`, the Streamer.bot redemption path is ignored so nothing is double-queued.) Native refunds
  only work on a reward **this app created/owns**.
- **Bits requests** — set `SrForBits=1`, `MinimumBitsForSr`, and optionally `SrForBitsKeyword`. A cheer at/above the
  threshold queues the cheer message (keyword stripped) as a request. Bits aren't refundable, so a rejected cheer just
  doesn't queue.

`RedemptionSource` defaults to `sb` (keep using Streamer.bot) — the native path is strictly opt-in.

## Configure from the dock

Most bot settings live on the dock's **Twitch bot** card: enable, mode (IRC / app), username, channel, client ID,
the **Connect Twitch** button (app mode), and the native-redemptions / manage-reward / bits toggles. **Secrets
(`TwitchBotToken`, `TwitchClientSecret`) are not entered in the dock** — put them in `config.txt` (the card shows
whether each is set). Click **Restart** (Engine card) after changing bot settings to reconnect.

## Config keys

| Key | Default | Notes |
|---|---|---|
| `TwitchBotEnabled` | `0` | master on/off |
| `TwitchAuthMode` | `irc` | `irc` or `app` |
| `TwitchBotUsername` / `TwitchBotToken` / `TwitchChannel` | – | **IRC mode**: account, chat token (`chat:read`+`chat:edit`), channel (no `#`) |
| `TwitchClientId` / `TwitchClientSecret` | – | **app mode**: your Twitch dev app credentials |
| `TwitchRedirectPort` | `3737` | **app mode**: loopback port for the OAuth callback (must match the app's Redirect URL) |
| `CommandPrefix` | `!` | character that starts a command |
| `VoteSkipCount` | `3` | distinct voters needed for `!voteskip` |
| `RedemptionSource` | `sb` | `sb` (Streamer.bot) or `twitch` (native EventSub, app mode) |
| `ManageReward` | `0` | app mode: reuse/create the `RewardName` channel-point reward |
| `RewardCost` | `100` | points cost when creating the reward |
| `SrForBits` | `0` | app mode: allow bits/cheer requests |
| `MinimumBitsForSr` | `100` | minimum bits to trigger a request |
| `SrForBitsKeyword` | *(empty)* | if set, text before it in the cheer is ignored |

## Default commands

Edit from the dock's **Commands** card (enable/disable, triggers, min level, cooldowns, response — changes apply **live**,
no restart), or by hand in `%AppData%\SongRequests\commands.json` (created on first run). Fields: `name`, `triggers`
(aliases, **no prefix**), `enabled`, `level` (min user level), `cd` (global cooldown s), `ucd` (per-user cooldown s), `resp` (response template).

| Command | Aliases | Level | Does |
|---|---|---|---|
| `!sr <song>` | `!songrequest` `!request` | everyone | request a song (same routing + Phase 2 gates as a redemption) |
| `!song` | `!nowplaying` `!np` | everyone | what's playing now |
| `!next` | | everyone | what's up next |
| `!queue` | `!q` | everyone | the next few in the queue |
| `!pos` | `!position` | everyone | how many songs you have queued |
| `!remove` | `!wrongsong` `!oops` | everyone | drop your last request |
| `!voteskip` | | everyone | vote to skip (needs `VoteSkipCount` voters) |
| `!skip` | | mod | skip the current song |
| `!vol [n]` | `!volume` | mod | show/set the YouTube overlay volume |
| `!play` / `!pause` | `!resume` | mod | resume / pause playback |
| `!togglesr` | `!srtoggle` | mod | turn song requests on/off |
| `!bansong` | `!blocksong` | mod | block + skip the current song |
| `!commands` | `!cmds` `!help` | everyone | list commands |

## User levels

From chat badges: **0** everyone · **1** subscriber · **2** VIP · **3** moderator · **4** broadcaster.
Mods and the broadcaster bypass command cooldowns. (Follower / sub-*tier* granularity would need extra Helix calls.)

## Response tokens

`{user}` `{song}` `{next}` `{queue}` `{pos}` `{vol}` `{state}` `{commands}` `{votes}` — unknown tokens are left as-is.

## Notes / limits (this phase)

- Chat `!sr` is **free** (no channel points), so nothing is refunded — it just replies with the result. It still respects the
  master on/off switch and all Phase 2 gates (blocklists, limits, cooldowns).
- `!vol` controls the **YouTube overlay** volume (the app's own knob); Spotify's own volume is separate.
- App mode currently authorizes a **single account** (posts + reads as the broadcaster). A separate bot account (needs the
  broadcaster to grant `channel:bot`) can come later.
- Native **rewards, refunds, and bits** work in app mode (see above). **Polls** (e.g. a Twitch poll-driven voteskip) are not implemented yet.
