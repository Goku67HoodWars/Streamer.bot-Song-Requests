<div align="center">

<img src="https://raw.githubusercontent.com/Goku67HoodWars/Streamer.bot-Song-Requests/main/docs/icon.png" width="92" alt="Song Requests" />

# Song Requests

**Let your Twitch viewers redeem channel points to queue songs — to your Spotify _or_ YouTube, played through OBS.**

No-admin installer · tiny auto-updates · run the whole thing from a dock inside OBS. Spotify optional.

<br/>

[![Download](https://img.shields.io/badge/⬇_Download_the_installer-7C3AED?style=for-the-badge)](https://github.com/Goku67HoodWars/Streamer.bot-Song-Requests/releases/latest/download/SongRequests-Setup.exe)

![Latest release](https://img.shields.io/github/v/release/Goku67HoodWars/Streamer.bot-Song-Requests?color=1DB954&label=version)
![Downloads](https://img.shields.io/github/downloads/Goku67HoodWars/Streamer.bot-Song-Requests/total?color=1DB954)
![Windows](https://img.shields.io/badge/Windows-10_%2F_11-0078D6?logo=windows11&logoColor=white)
![Twitch + Streamer.bot](https://img.shields.io/badge/Twitch-Streamer.bot-9146FF?logo=twitch&logoColor=white)

<br/>

<img src="https://raw.githubusercontent.com/Goku67HoodWars/Streamer.bot-Song-Requests/main/docs/app-setup.png" width="760" alt="The whole app: one setup page that shows you every step and turns each one green when it's done" />

</div>

---

## ✨ Features

- 🎵 **Spotify _and_ YouTube** — viewers redeem the reward (or use a chat command) and type a song name, a Spotify/YouTube link, or `yt` / `sp` to force a platform. Bare names go to Spotify when it's connected, YouTube otherwise.
- 🟢 **Spotify optional** — no Premium? Run **YouTube-only**; the engine boots and works without ever connecting Spotify.
- 💬 **Built-in chat commands** *(optional)* — a native Twitch bot adds `!sr`, `!song`, `!queue`, `!pos`, `!skip`, `!voteskip` and more, with per-command user levels + cooldowns. Runs **alongside** Streamer.bot. → [guide](docs/twitch-bot.md)
- 🎯 **Native rewards & bits** *(optional — app mode)* — the bot can create/manage the channel-point reward, handle redemptions, auto-refund failures, and take **bits** requests, with **no Streamer.bot required**. → [guide](docs/twitch-bot.md)
- 🧹 **Queue governance** — user / song / artist **blocklists**, max-queue + per-user **limits**, global + per-user **cooldowns**, and an **explicit-track** filter — all live from the dock. → [guide](docs/queue-governance.md)
- 📝 **On-stream now-playing files** — writes `nowplaying.txt` + `cover.png` (plus split artist / title / requester) for OBS **Text/Image** sources, with custom format tokens — no browser source needed. → [guide](docs/obs-text-output.md)
- 🎛️ **A control dock inside OBS** — toggles, volume, max length, play/pause/skip, the live queue, **plus** the rules, blocklists, output, bot, and command editors — all in one browser dock. The app itself is **one setup page** that turns each step green when it's done.
- ↩️ **Auto-refunds** — a blocked, rate-limited, or failed request refunds the points and tells the viewer why.
- ⚡ **Tiny auto-updates** — the .NET runtime is bundled once, so version updates download in **~0.3 MB**, not the whole app.
- 🔒 **Yours, on your machine** — every streamer uses their own keys. Nothing is hosted, nothing phones home.

---

## 📸 A look inside

Once you're set up, you run the whole show from the **Song Queue dock inside OBS** — requests on/off, volume, max song length, play / pause / skip, and the live queue, all in one panel. The desktop app is only ever needed for the one-page setup above.

### 🎬 Now Playing overlay

<p align="center">
  <img src="https://raw.githubusercontent.com/Goku67HoodWars/Streamer.bot-Song-Requests/main/docs/overlay.png" width="720" alt="Now Playing overlay" />
</p>

Drag `nowplaying.html` into OBS (or add a Browser source pointing at it) and size it **640 × 150**. It auto-sizes to the song name and fades out when nothing's playing.

---

## 🚀 Setup (about 10 minutes)

> **You'll need:** **OBS** · Windows · and a way to receive requests — either **Streamer.bot** (the classic channel-point flow) **or** the built-in **native Twitch bot** (chat commands out of the box; app mode can even run channel points + bits on its own). Plus **Spotify Premium** _only if you want the Spotify lane_ (it runs **YouTube-only** without it).

**How it connects:** Twitch → **Streamer.bot** (`127.0.0.1:8080`) → **the engine** (`127.0.0.1:8090`, auto-started by OBS) → **OBS** plays the audio. Three apps on your own machine — nothing hosted, nothing phones home.

1. **Install** — run `SongRequests-Setup.exe`. It installs to your user folder (**no admin, no location to pick**), makes a desktop shortcut, and opens the app. *(SmartScreen → More info → Run anyway.)*
2. **Streamer.bot** → *Servers/Clients → WebSocket Server* → enable it (**Auto Start** on, `127.0.0.1:8080`, auth **off**). Keep it open whenever you stream.
3. **Make a Channel Points reward** with **“Require viewer to enter text”** on — ideally **in Streamer.bot**, so the app can auto-**refund** failed requests and **pause** the reward when you turn requests off.
4. **In the app** → set the **reward name** to match your reward exactly.
   - **Spotify (optional):** make a free app at [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) — Redirect URI `http://127.0.0.1:8888/callback`, tick **Web API** — then paste the **Client ID + Secret** and click **Connect Spotify** *(needs Premium)*. **Or skip it → YouTube-only.**
   - **Chat replies (optional):** hit the chat-setup button and paste the copied `Song Requests Announce` action into Streamer.bot (*Actions → Import*).
5. **In OBS** — two sources + one script:
   - **Audio player** (required): the **auto-start script creates this for you** — a Browser source at `http://127.0.0.1:8090/youtube` with **Control audio via OBS** + **Monitor and Output**, so **you *and* the stream hear it**. (If you ever add it by hand, set its **Audio Monitoring → Monitor and Output**.) It stays **blank on screen** — it only plays the songs.
   - **Control dock:** *View → Docks → Custom Browser Docks…*, URL `http://127.0.0.1:8090/dock`. This is your control center.
   - **Auto-start:** the app's **“Auto-start with OBS”** copies a script path → *OBS → Tools → Scripts → + → paste → Open*. The engine now runs whenever OBS is open.
   - **Sound:** click **“Fix YouTube sound in OBS”** once, then fully reopen OBS.
6. **Go live** — open OBS (the engine auto-starts) + Streamer.bot, and run everything from the **dock**: on/off toggles, volume, **max song length**, play/pause/skip, and the live queue.

**Viewers** redeem the reward and type a **song name** (→ Spotify if connected, else YouTube), a **Spotify / YouTube link**, or `yt song` / `sp song` to force a platform. A request to a lane that's off or not connected is **auto-refunded** with a chat heads-up. Reopen the app from the **Start menu** anytime — it's a single setup page now, and it updates itself every time you open it.

---

## 📚 Optional add-ons & feature guides

Beyond the core request queue, the dock and `config.txt` unlock three bigger feature sets — each with its own guide:

- 📝 **[OBS now-playing files](docs/obs-text-output.md)** — `nowplaying.txt` + `cover.png` (and split artist/title/requester) for OBS Text/Image sources, with format tokens.
- 🧹 **[Queue governance](docs/queue-governance.md)** — blocklists (user / song / artist), max-queue + per-user limits, global + per-user cooldowns, explicit-track filter.
- 💬 **[Native Twitch bot](docs/twitch-bot.md)** — chat commands (`!sr`, `!song`, `!queue`, `!skip`, `!voteskip`, …) with user levels + cooldowns; in **app mode**, native channel-point rewards + bits with no Streamer.bot.

<details>
<summary><b>🎚️ Running the queue</b></summary>

<br/>

The **engine** is the background worker that resolves requests, adds Spotify songs / downloads YouTube audio, and drives the OBS player. Once you've added the auto-start script, it **runs whenever OBS is open** — no clicking. You can also Start/Restart it from the app's status screen or the dock. Keep **Streamer.bot** open (redemptions come through it), and for Spotify requests keep **Spotify** open and playing on a device.

</details>

<details>
<summary><b>🗂️ History &amp; statuses</b></summary>

<br/>

The **History** tab lists every request — time, user, text, resolved track, status, and when it played:

- `queued` — added to the queue
- `played` — confirmed played
- `scammed` — still queued when the stream ended (never played 😅)
- `not found` / `no device` — couldn't queue

</details>

<details>
<summary><b>🔄 Updating &amp; uninstalling</b></summary>

<br/>

- **Updates** install themselves every time you open the app **or** open OBS (tiny ~0.3 MB delta). There's nothing to click.
- **Uninstall** from Windows **Settings → Apps → Installed apps** (search *Song Requests*), or the **Uninstall** link in the app. **Clear data** wipes settings + history without uninstalling.

</details>

<details>
<summary><b>🛠️ Build from source</b></summary>

<br/>

Requires the .NET SDK. Installed layout keeps the folder clean: a small **launcher** `SongRequests.exe` sits in the root and forwards to the real app in `app\`. The core is one self-contained exe that runs three ways — the config window (no args), the queue engine (`--engine`), and the stop signal (`--stop`) — published as loose files (not single-file, which is what keeps updates tiny). The installer is a single-file exe.

```bash
dotnet publish engine/source/ConfigGUI/SongRequests.csproj          -c Release -r win-x64   # core -> app\
dotnet publish engine/source/Launcher/SongRequests-Launcher.csproj  -c Release -r win-x64   # root launcher
dotnet publish installer/SongRequests-Setup.csproj           -c Release -r win-x64   # setup
```

Releases are packaged as `app.zip` (launcher-less app + assets) + `runtime.zip` (launcher + bundled runtime) + `manifest.json`, so the updater fetches only what changed. Settings/logs live in `%AppData%\SongRequests`.

</details>

<details>
<summary><b>❓ Troubleshoot</b></summary>

<br/>

- **"no active device"** → open Spotify and press play.
- **Nothing queues** → the reward name must match Twitch **exactly**, and the queue must be running.
- **Still nothing** → make sure Streamer.bot is open with the WebSocket Server on (or run requests natively via the bot's app mode).
- **Chat commands do nothing** → enable + connect the native bot ([guide](docs/twitch-bot.md)); the log should say `Twitch bot: connected`.
- **No now-playing text file** → the dock's **OBS now-playing files** toggle must be on; files live in `%AppData%\SongRequests\obs`.
- **Windows SmartScreen warning** (unsigned app) → *More info → Run anyway*.

</details>

---

<div align="center">
<sub>Not affiliated with Spotify, Twitch, or OBS. Spotify requests need Spotify Premium; YouTube-only needs neither. The Streamer.bot flow needs no Twitch developer app; the native bot's app mode (rewards/bits) uses your own Twitch app.</sub>
</div>
