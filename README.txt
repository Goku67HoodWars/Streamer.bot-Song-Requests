==================================================
  SONG REQUESTS  -  QUICK START
==================================================

Viewers redeem Twitch channel points and type a song. It gets
played through OBS - from your Spotify OR from YouTube.

Spotify is OPTIONAL: with Spotify Premium you get a Spotify lane;
without it, everything still works YouTube-only (viewers just type
a song name or paste a YouTube link).

Installed with SongRequests-Setup.exe - it updates itself every time
you open the app or open OBS. Reopen anytime: search "Song Requests"
in the Windows Start menu. The app is just SETUP; once you're set up,
you run everything from the dock inside OBS.

WHAT'S IN THIS FOLDER
  SongRequests.exe    <- open this for the one-page setup
  obs-autostart.lua   <- the OBS script that runs everything (see step 4)
  nowplaying.html     <- optional "now playing" overlay (see below)
  dock.html           <- the in-OBS control dock (added for you in step 5)
  app\                <- program files + .NET runtime (leave it alone)

YOU NEED:  OBS + Windows, and a way to receive requests - either Streamer.bot
           (the classic channel-point flow) OR the built-in native Twitch bot
           (chat commands; app mode can run channel points + bits on its own).
           Spotify Premium ONLY if you want the Spotify lane.

Open SongRequests.exe and follow the one page, top to bottom. Each step
turns green on its own when it's done. In short:

--------------------------------------------------
1) CONNECT SPOTIFY   (OPTIONAL - skip for YouTube-only)
--------------------------------------------------
   a) https://developer.spotify.com/dashboard  -> log in -> Create app
   b) Redirect URI, paste EXACTLY:  http://127.0.0.1:8888/callback
   c) Tick "Web API", Save.  Settings -> copy Client ID + Secret.
   d) Paste them in the app and click "Connect Spotify".
   Leave the boxes empty to run YouTube-only.

--------------------------------------------------
2) CREATE THE REWARD  -  IN STREAMER.BOT (not the Twitch website)
--------------------------------------------------
Only the app that creates a reward can refund/pause it, so make it
in Streamer.bot:  Platforms -> Twitch -> Channel Point Rewards -> New.
   - Name it (e.g. "Song Request")
   - Turn ON "Require viewer to enter text"
   - Set a cost, Save.
Then type that EXACT reward name into the app.

--------------------------------------------------
3) STREAMER.BOT: turn on the WebSocket Server
--------------------------------------------------
Servers/Clients -> WebSocket Server -> Auto Start ON, 127.0.0.1,
port 8080, endpoint /, Authentication Off.  Status must say "Running".
Keep Streamer.bot open whenever you stream.

--------------------------------------------------
4) SET UP OBS  -  one script does it all
--------------------------------------------------
In the app click "Copy OBS script path", then in OBS:
   Tools -> Scripts -> the + button -> paste the path -> Open.
That one script starts the engine whenever OBS is open and creates
the invisible "YouTube Player" audio source. Then click
"Fix YouTube sound in OBS" once, and fully reopen OBS.

--------------------------------------------------
5) ADD THE CONTROL DOCK
--------------------------------------------------
Close OBS, click "Add the dock to OBS" in the app, reopen OBS, then
click Docks (top-left menu) -> turn on "Song Queue". That dock is your
control panel: requests on/off, volume, max length, skip, live queue.

For Spotify requests, keep Spotify open and playing on a device.

--------------------------------------------------
NOW-PLAYING OVERLAY  (optional)
--------------------------------------------------
Show the current song on stream (works for Spotify AND YouTube):
   - In OBS add a Browser source (or drag nowplaying.html into a scene)
   - Point it at  nowplaying.html  (in this folder), size 640 x 150.
Want a custom look? In the app, "Optional extras" -> "Make your own"
opens a guide (data fields + a prompt to have an AI build the HTML).

--------------------------------------------------
MORE FEATURES  (full guides in the docs/ folder)
--------------------------------------------------
- OBS now-playing FILES: nowplaying.txt + cover.png for OBS Text/Image
  sources, no browser needed.             -> docs/obs-text-output.md
- QUEUE RULES + BLOCKLISTS: block users/songs/artists; set max-queue /
  per-user limits + cooldowns; filter explicit. All from the dock.
                                          -> docs/queue-governance.md
- NATIVE TWITCH BOT: chat commands (!sr, !song, !queue, !skip, !voteskip,
  ...) with levels + cooldowns; app mode can run channel-point rewards +
  bits with no Streamer.bot.              -> docs/twitch-bot.md

--------------------------------------------------
TROUBLESHOOT
--------------------------------------------------
Settings + logs live in:  %AppData%\SongRequests
   nothing queues   -> reward name must match Twitch exactly
   Spotify "no device" -> open Spotify and press play
   still nothing    -> Streamer.bot open with the WebSocket Server ON?
