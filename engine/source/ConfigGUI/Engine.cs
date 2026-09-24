using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// ===== Song Request engine =====
// Runs headless as  SongRequests.exe --engine . Reads the settings saved by the GUI,
// connects to Streamer.bot, and queues requested songs in Spotify. (Uses the shared Req class.)
partial class Engine
{
    const string MutexName = "SongRequestsEngineSingleton_v1";
    const string StopName = "SongRequestsEngineStop_v1";

    static readonly HttpClient Http = new HttpClient();
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SongRequests");
    static string ConfigFile => Path.Combine(DataDir, "config.txt");
    static string TokenFile  => Path.Combine(DataDir, "token.txt");
    static string LogFile    => Path.Combine(DataDir, "log.txt");
    static string RequestsFile => Path.Combine(DataDir, "requests.json");

    static readonly List<Req> Reqs = new List<Req>();
    static readonly List<Req> Pending = new List<Req>();
    static readonly object _rl = new object();

    static string ClientId, ClientSecret, RewardName, WsUrl, RefreshToken, AccessToken;
    static DateTime AccessExpires = DateTime.MinValue;
    static Mutex _single;
    // Chat announce: the engine asks Streamer.bot (which owns the Twitch connection) to post the queue
    // result via DoAction over the same socket it reads redemptions from. AnnounceAction = the SB action name.
    static bool AnnounceChat; static string AnnounceAction = "Song Requests Announce";
    // Refund a failed request + pause the reward when requests are off - both via Streamer.bot DoAction
    // (Twitch only lets the app that CREATED the reward manage it, so the reward must be SB-created).
    static bool RefundFailed = true; static string RefundAction = "Song Requests Refund";        // refund is a default behavior now (no dock toggle)
    static bool PauseReward = true; static string RewardOnAction = "Song Requests Reward On", RewardOffAction = "Song Requests Reward Off";  // auto, follows the master/lanes
    static int _rewardApplied = -1;                          // last reward state pushed to SB: -1 unknown, 0 paused, 1 on
    static readonly SemaphoreSlim _rewardLock = new SemaphoreSlim(1, 1);   // serialize reward pushes so _rewardApplied can't desync from SB
    static bool _restarting;                                 // cmd=restart set this so GracefulStop won't pause the reward (the relaunch resyncs it)
    static ClientWebSocket _sbWs;                            // the live Streamer.bot socket (for sending DoAction)
    static readonly SemaphoreSlim _wsSend = new SemaphoreSlim(1, 1);

    static int NpPort = 8090;
    static string NowJson = "{\"playing\":false}";
    static readonly object _np = new object();

    // ---- OBS file outputs (now-playing text + cover art for OBS Text/Image sources; see PublishOutputs) ----
    static bool OutputEnabled = true;                    // master switch for the file outputs
    static string OutputDir;                             // where the files are written (resolved in LoadConfig)
    static string OutputFormat = "{artist} - {title}";  // nowplaying.txt template (tokens + {{requested-by}} block)
    static bool SplitOutput;                             // also write artist.txt / title.txt / requester.txt
    static string RequesterPrefix = "Requested by ";    // prepended to the requester name in requester.txt
    static bool DownloadCover = true;                    // save album/thumbnail art to cover.png
    static bool AppendSpaces; static int SpaceCount = 10; // trailing-space padding for marquee scrolling in OBS
    static string PauseBehavior = "nothing";            // nothing | clear | text  (what the files show while paused/idle)
    static string CustomPauseText = "";
    static readonly object _outLock = new object();
    static string _outLastKey, _outLastArtUrl;          // debounce so the disk isn't touched every poll
    static string OutDirDefault => Path.Combine(DataDir, "obs");

    // ---- queue governance (blocklists, limits, cooldowns, explicit filter) - all viewer-facing gates ----
    static int MaxQueueLength, MaxRequestsPerUser, SrCooldownSec, SrPerUserCooldownSec;   // 0 = unlimited/off
    static bool BlockExplicit;
    static volatile HashSet<string> UserBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // exact username match
    static volatile List<string> SongBlacklist = new List<string>();     // link/uri/id exact, or case-insensitive substring of "title - artist"
    static volatile List<string> ArtistBlacklist = new List<string>();   // case-insensitive substring of the artist name
    static readonly object _blLock = new object();                       // serialize blocklist file writes
    static DateTime _lastReqUtc = DateTime.MinValue;
    static readonly Dictionary<string, DateTime> _lastReqByUser = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    static readonly object _gateLock = new object();
    static string UserBlFile => Path.Combine(DataDir, "blacklist_users.txt");
    static string SongBlFile => Path.Combine(DataDir, "blacklist_songs.txt");
    static string ArtistBlFile => Path.Combine(DataDir, "blacklist_artists.txt");

    // ---- YouTube request lane (audio plays in the hidden OBS browser-source player; see youtube.html) ----
    // file = cached local audio path once downloaded; dlStarted/dlFailed track the background fetch so the
    // conductor never pauses Spotify into silence waiting on a download (or on a video that won't fetch).
    class YtItem { public string id, title, channel, user, file; public int durSec; public bool dlStarted, dlFailed, isVideo; }
    static bool RequestsEnabled = true, YtEnabled, SpotifyEnabled = true, SpotifyConnected; static int MaxSongSec = 360, MinSongSec = 20, YtVol = 70;   // RequestsEnabled = master; SpotifyConnected=false => boots YouTube-only
    static bool YtShowCard = true;                           // dock toggle: show the now-playing card, or run audio-only (transparent overlay)
    static bool YtVideo;                                     // dock toggle: fetch + show the actual video (progressive mp4) instead of audio-only
    static bool _ytPaused; static DateTime _ytPauseStart;    // dock paused the active YouTube item (+ when, to exclude paused time from the length cap)
    static int _ytSeekMs; static long _ytSeekId;             // dock seek request for the active YouTube item (player applies it when seekId changes)
    static bool _spotUserPaused;                             // streamer paused Spotify from the dock (vs Spotify just being idle) - don't auto-hand-off a queued item
    static readonly List<YtItem> YtQ = new List<YtItem>();
    static readonly object _yt = new object();
    static YtItem YtActive;                                  // item currently playing in the overlay player
    static long YtEpoch;                                     // bumps on play/stop so stale player reports are ignored
    static bool _skipToYt;                                   // dock Skip pressed with a YouTube item waiting -> play it now
    static DateTime _ytStarted, _lastPlayerSeen = DateTime.MinValue;
    static bool _resumeSpotify;                              // we paused Spotify for YouTube and owe it a resume
    static bool _spotSkipOnResume;                           // that paused Spotify song was SKIPPED by the user -> advance past it on resume, don't replay it
    // last Spotify state seen by NowPlayingLoop (drives the song-boundary handoff + the dock's up-next list)
    static bool _spotPlaying; static int _spotProgMs, _spotDurMs; static DateTime _spotSeenUtc = DateTime.MinValue;
    static readonly List<(string title, string artist, string art, string uri)> _spotNext = new List<(string, string, string, string)>();
    static readonly HashSet<string> _skipUris = new HashSet<string>(StringComparer.Ordinal);   // Spotify uris to auto-skip when they play (Spotify has no remove-from-queue API)
    static DateTime _lastMarkedSkip = DateTime.MinValue;   // when SkipWatcher last fired, so it keeps fast-polling through a run of consecutive marked songs
    // control-endpoint token: the served player/dock pages carry it; blocks a random web page the
    // streamer visits from driving the queue via localhost (loopback bind alone isn't a defense).
    static string CtlToken = "";
    static string ResumeMarker => Path.Combine(DataDir, "resume_owed.txt");   // survives a crash/kill mid-YouTube
    static string ShowCardFile => Path.Combine(DataDir, "showcard.txt");      // persists the dock's show-card toggle across restarts
    static string ShowVideoFile => Path.Combine(DataDir, "showvideo.txt");    // persists the dock's show-video toggle across restarts

    // ---- cross-process control (GUI / --stop talk to a running --engine) ----
    public static bool IsRunning()
    {
        try { using var m = Mutex.OpenExisting(MutexName); return true; } catch { return false; }
    }
    public static void SignalStop()
    {
        try { using var ev = EventWaitHandle.OpenExisting(StopName); ev.Set(); } catch { }
    }

    public static async Task RunEngine()
    {
        // Wait briefly to ACQUIRE the single-instance mutex instead of giving up immediately: on a dock
        // Restart the outgoing engine can hold it for a few seconds (GracefulStop resuming Spotify), and the
        // relaunched engine must not lose that race and silently exit (which would brick the restart).
        _single = new Mutex(false, MutexName);
        bool owned;
        try { owned = _single.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { owned = true; }   // previous instance died without releasing -> we own it now
        if (!owned) return;   // another engine instance is genuinely running

        var stopEvt = new EventWaitHandle(false, EventResetMode.ManualReset, StopName, out bool evNew);
        if (!evNew) stopEvt.Reset();
        _ = Task.Run(() => { try { stopEvt.WaitOne(); } catch { } GracefulStop(); Environment.Exit(0); });   // exit when the GUI / --stop asks

        Directory.CreateDirectory(DataDir);
        YouTube.OnLog = Log;         // surface the one-time ffmpeg fetch (HD video) in the engine log
        YouTube.ClearAudioCache();   // stale cached media from a previous run (the queue never survives a restart)
        try { if (File.Exists(ShowCardFile)) YtShowCard = File.ReadAllText(ShowCardFile).Trim() != "0"; } catch { }
        try { if (File.Exists(ShowVideoFile)) YtVideo = File.ReadAllText(ShowVideoFile).Trim() == "1"; } catch { }
        CtlToken = Guid.NewGuid().ToString("N");
        LoadRequests();
        Log("=== engine starting ===");

        if (!LoadConfig())
        { Log("Not set up yet - open Song Requests and set it up. Exiting."); return; }
        LoadBlacklists();   // user/song/artist blocklists (one entry per line, files in the data dir)

        // Spotify is OPTIONAL. Connect it if it's set up; otherwise boot YouTube-only. The router defaults to
        // Spotify only when connected+on (else YouTube), and refunds a Spotify-targeted request with a message.
        SpotifyConnected = false;
        if (!string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
            && File.Exists(TokenFile) && File.ReadAllText(TokenFile).Trim().Length > 0)
        {
            RefreshToken = File.ReadAllText(TokenFile).Trim();
            try { await RefreshAccess(); SpotifyConnected = true; Log("Spotify connected - bare requests default to Spotify."); }
            catch (Exception e) { Log("Spotify login expired (" + e.Message + ") - running YouTube-only until you reconnect in the app."); }
        }
        else Log("Spotify not connected - running YouTube-only. Enter your keys + Connect Spotify in the app to enable Spotify requests.");

        // If a previous run was killed/crashed while a YouTube item had Spotify paused, resume it now.
        try { if (SpotifyConnected && File.Exists(ResumeMarker)) { File.Delete(ResumeMarker); await SpotifyPlayback(true); Log("Recovered: resumed Spotify (it was paused for a YouTube item at last shutdown)."); } } catch { }

        if (OutputEnabled)
        {
            try { Directory.CreateDirectory(OutputDir); } catch { }
            try { foreach (var f in new[] { "nowplaying.txt", "artist.txt", "title.txt", "requester.txt" }) { var p = Path.Combine(OutputDir, f); if (!File.Exists(p)) File.WriteAllText(p, ""); } } catch { }
            Log("OBS text files -> " + OutputDir + "  (point an OBS Text source at nowplaying.txt; an Image source at cover.png)");
        }
        _ = PlayedWatcher();   // marks when queued songs actually play
        _ = NowPlayingLoop();  // caches current track for the overlay
        _ = YtConductor();     // hands playback between Spotify and the YouTube overlay player
        _ = SkipWatcher();     // skips "skip-when-it-plays" Spotify songs within ~0.4s instead of ~2.5s
        _ = Task.Run(() => NowServer());  // serves the overlay/player/dock endpoints on 127.0.0.1:NpPort
        _ = TwitchChatLoop();   // native Twitch chat bot for viewer commands (!sr, !song, ...) - independent of Streamer.bot
        await RunLoop();
    }

    static bool LoadConfig()
    {
        if (!File.Exists(ConfigFile)) return false;
        var c = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(ConfigFile))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
            int i = line.IndexOf('='); if (i < 0) continue;
            c[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }
        ClientId = Cfg(c, "SpotifyClientId"); ClientSecret = Cfg(c, "SpotifyClientSecret");
        RewardName = Cfg(c, "RewardName", "Song Request");
        WsUrl = Cfg(c, "StreamerBotWs", "ws://127.0.0.1:8080/");
        NpPort = int.TryParse(Cfg(c, "NowPlayingPort", "8090"), out var npp) ? npp : 8090;
        YtEnabled = Cfg(c, "YouTubeEnabled", "1") == "1";   // default ON: a fresh install (no key yet) must boot with a usable YouTube lane, Spotify or not
        SpotifyEnabled = Cfg(c, "SpotifyEnabled", "1") == "1";
        RequestsEnabled = Cfg(c, "RequestsEnabled", "1") == "1";
        AnnounceChat = Cfg(c, "AnnounceChat", "0") == "1";
        AnnounceAction = Cfg(c, "AnnounceAction", "Song Requests Announce");
        RefundFailed = Cfg(c, "RefundFailed", "1") == "1";
        RefundAction = Cfg(c, "RefundAction", "Song Requests Refund");
        PauseReward = Cfg(c, "PauseReward", "1") == "1";
        RewardOnAction = Cfg(c, "RewardOnAction", "Song Requests Reward On");
        RewardOffAction = Cfg(c, "RewardOffAction", "Song Requests Reward Off");
        MaxSongSec = int.TryParse(Cfg(c, "MaxSongSeconds", "360"), out var mx) ? Math.Max(30, mx) : 360;
        MinSongSec = int.TryParse(Cfg(c, "MinSongSeconds", "20"), out var mn) ? mn : 20;
        YtVol = int.TryParse(Cfg(c, "YouTubeVolume", "70"), out var yv) ? Math.Clamp(yv, 0, 100) : 70;
        OutputEnabled = Cfg(c, "OutputEnabled", "1") == "1";
        OutputDir = Cfg(c, "OutputDir", "");
        if (string.IsNullOrWhiteSpace(OutputDir)) OutputDir = OutDirDefault;
        OutputFormat = Cfg(c, "OutputFormat", "{artist} - {title}");
        SplitOutput = Cfg(c, "SplitOutput", "0") == "1";
        RequesterPrefix = Cfg(c, "RequesterPrefix", "Requested by ");
        DownloadCover = Cfg(c, "DownloadCover", "1") == "1";
        AppendSpaces = Cfg(c, "AppendSpaces", "0") == "1";
        SpaceCount = int.TryParse(Cfg(c, "SpaceCount", "10"), out var spc) ? Math.Clamp(spc, 0, 200) : 10;
        PauseBehavior = Cfg(c, "PauseBehavior", "nothing").ToLowerInvariant();
        CustomPauseText = Cfg(c, "CustomPauseText", "");
        MaxQueueLength = int.TryParse(Cfg(c, "MaxQueueLength", "0"), out var mql) ? Math.Max(0, mql) : 0;
        MaxRequestsPerUser = int.TryParse(Cfg(c, "MaxRequestsPerUser", "0"), out var mru) ? Math.Max(0, mru) : 0;
        SrCooldownSec = int.TryParse(Cfg(c, "SrCooldownSec", "0"), out var scd) ? Math.Max(0, scd) : 0;
        SrPerUserCooldownSec = int.TryParse(Cfg(c, "SrPerUserCooldownSec", "0"), out var pcd) ? Math.Max(0, pcd) : 0;
        BlockExplicit = Cfg(c, "BlockExplicit", "0") == "1";
        LoadTwitchConfig(c);   // native Twitch chat bot settings + command definitions (see TwitchBot.cs)
        return true;   // Spotify keys are no longer required to boot - a config file is enough (YouTube-only if Spotify isn't set up)
    }

    static string Cfg(Dictionary<string, string> c, string k, string def = "")
        => c.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

    // Persist a single setting to config.txt (read-modify-write) so a dock change survives restarts and the
    // GUI sees it. Best-effort; the GUI owns config.txt but doesn't hold it open, so a rare concurrent save is
    // the only risk, and both sides write whole-file.
    static readonly object _cfgWrite = new object();
    static void SetConfigValue(string key, string value)
    {
        try
        {
            lock (_cfgWrite)
            {
                var lines = File.Exists(ConfigFile) ? new List<string>(File.ReadAllLines(ConfigFile)) : new List<string>();
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].TrimStart().StartsWith("#")) continue;
                    int eq = lines[i].IndexOf('=');
                    if (eq > 0 && lines[i].Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    { lines[i] = key + "=" + value; found = true; break; }
                }
                if (!found) lines.Add(key + "=" + value);
                File.WriteAllLines(ConfigFile, lines);
            }
        }
        catch { }
    }

    static async Task RefreshAccess()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + ClientSecret)));
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { {"grant_type","refresh_token"}, {"refresh_token",RefreshToken} });
        using var resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception((int)resp.StatusCode + ": " + body);
        var json = JsonDocument.Parse(body).RootElement;
        AccessToken = json.GetProperty("access_token").GetString();
        AccessExpires = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32() - 60);
        if (json.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
        { RefreshToken = rt.GetString(); File.WriteAllText(TokenFile, RefreshToken); }
    }

    static readonly SemaphoreSlim _tokLock = new SemaphoreSlim(1, 1);
    static async Task<string> Token()
    {
        if (DateTime.UtcNow < AccessExpires) return AccessToken;
        await _tokLock.WaitAsync();
        try { if (DateTime.UtcNow >= AccessExpires) await RefreshAccess(); return AccessToken; }
        finally { _tokLock.Release(); }
    }

    // Routes a request to the right service. Default is Spotify (unchanged behavior); a YouTube /
    // YouTube Music link or a "yt <text>" prefix goes to the YouTube lane; "sp <text>" forces Spotify.
    // fromStreamer: the dock's own add box - the streamer isn't a viewer, so the requests-off gate doesn't apply.
    static async Task<(string status, string track, string uri)> QueueSong(string input, string user = "", bool fromStreamer = false)
    {
        if (!RequestsEnabled && !fromStreamer) { Log("  Ignored - song requests are turned off."); return ("requests off", null, null); }
        // Viewer-facing gates (blocklist / cooldown / queue + per-user limits). The streamer's own dock add skips them.
        if (!fromStreamer) { var gate = CheckGate(user); if (gate != null) { Log("  Rejected (" + gate + ") for " + user + "."); return (gate, null, null); } }
        string s = (input ?? "").Trim();
        // Explicit target wins: "sp "/"yt " prefix, or a platform link. Each queue fn refunds with a message
        // if that platform isn't available (Spotify not connected/off, or YouTube off).
        (string status, string track, string uri) res;
        if (s.StartsWith("sp ", StringComparison.OrdinalIgnoreCase)) res = await QueueSpotify(s.Substring(3).Trim());
        else if (s.StartsWith("yt ", StringComparison.OrdinalIgnoreCase)) res = await QueueYouTube(s.Substring(3).Trim(), user);
        else if (YouTube.IsLink(s)) res = await QueueYouTube(s, user);
        else if (ExtractTrackUri(s) != null) res = await QueueSpotify(s);   // a Spotify track link/URI -> Spotify
        // No explicit target -> default to the USABLE lane: Spotify when it's connected AND switched on,
        // else YouTube (which refunds "youtube off" itself if that lane is off too). A bare name must never
        // refund just because the streamer toggled Spotify off while YouTube could have served it.
        else if (SpotifyConnected && SpotifyEnabled) res = await QueueSpotify(s);
        else if (SpotifyConnected && !YtEnabled) res = await QueueSpotify(s);   // both lanes off / YT off -> keep the clearer "spotify off" message
        else res = await QueueYouTube(s, user);
        if (!fromStreamer && res.status == "queued") MarkRequested(user);   // only a successful viewer request starts the cooldown(s)
        return res;
    }

    static async Task<(string status, string track, string uri)> QueueYouTube(string q, string user)
    {
        if (!YtEnabled) { Log("  YouTube request ignored - YouTube requests are turned off in the app."); return ("youtube off", null, null); }
        var m = YouTube.IsLink(q) || YouTube.ExtractId(q) != null ? await YouTube.ResolveAsync(q) : await YouTube.SearchAsync(q);
        if (!m.Ok) { Log("  YouTube: " + (m.Error ?? "no result")); return ("not found", null, null); }
        string uri = "youtube:video:" + m.VideoId;
        string label = m.Title + (string.IsNullOrEmpty(m.Channel) ? "" : " - " + m.Channel);
        if (m.IsLive || m.DurationSec <= 0) { Log("  Rejected (live stream): " + label); return ("no live", label, uri); }
        if (m.AgeLimit > 0) { Log("  Rejected (age-restricted): " + label); return ("age blocked", label, uri); }
        if (m.DurationSec > MaxSongSec) { Log($"  Rejected (too long, {m.DurationSec}s > {MaxSongSec}s): " + label); return ("too long", label, uri); }
        if (m.DurationSec < MinSongSec) { Log("  Rejected (too short): " + label); return ("too short", label, uri); }
        if (IsArtistBlocked(m.Channel)) { Log("  Rejected (artist blocked): " + label); return ("artist blocked", label, uri); }
        if (IsSongBlocked(uri, label)) { Log("  Rejected (song blocked): " + label); return ("song blocked", label, uri); }
        bool playerAlive; YtItem item;
        lock (_yt)
        {
            if ((YtActive != null && YtActive.id == m.VideoId) || YtQ.Exists(x => x.id == m.VideoId))
            { Log("  Duplicate YouTube request: " + label); return ("duplicate", label, uri); }
            item = new YtItem { id = m.VideoId, title = m.Title, channel = m.Channel, durSec = m.DurationSec, user = user };
            YtQ.Add(item);
            playerAlive = (DateTime.UtcNow - _lastPlayerSeen).TotalSeconds <= 5;
        }
        _ = EnsureMedia(item);   // start fetching now so it's ready to play the moment its turn comes
        Log("  QUEUED (YouTube): " + label);
        if (!playerAlive) Log("  Note: the YouTube player isn't loaded in OBS - it will play once the player source is added/visible.");
        return ("queued", label, uri);
    }

    // Fetch an item's audio once (idempotent): sets it.file on success, it.dlFailed if it can't be fetched.
    // The conductor waits on it.file before pausing Spotify, so a slow download never causes dead air.
    static async Task EnsureMedia(YtItem it)
    {
        bool start, wantVid;
        // Lock in the video/audio mode at fetch time so a mid-queue toggle can't leave this item half-downloaded as the wrong type.
        lock (_yt) { start = !it.dlStarted; if (start) { it.dlStarted = true; it.isVideo = YtVideo; } wantVid = it.isVideo; }
        if (!start) return;
        string path = await YouTube.DownloadMediaAsync(it.id, wantVid);
        lock (_yt) { it.file = path; it.dlFailed = path == null; }
        if (path == null) Log("  Couldn't fetch " + (wantVid ? "video" : "audio") + " for: " + it.title + " (it'll be skipped when its turn comes).");
    }

    static DateTime _lastSpotReconnect = DateTime.MinValue;
    static readonly SemaphoreSlim _reconnLock = new SemaphoreSlim(1, 1);
    // Self-heal: if a Spotify request arrives while disconnected, re-read the (possibly just-updated) keys +
    // refresh token from disk and try to authenticate - so reconnecting in the app takes effect WITHOUT an
    // engine restart. Throttled so a burst of requests can't hammer the token endpoint.
    static async Task<bool> TryReconnectSpotify()
    {
        if (SpotifyConnected) return true;
        if ((DateTime.UtcNow - _lastSpotReconnect).TotalSeconds < 20) return false;
        await _reconnLock.WaitAsync();
        try
        {
            if (SpotifyConnected) return true;
            if ((DateTime.UtcNow - _lastSpotReconnect).TotalSeconds < 20) return false;
            _lastSpotReconnect = DateTime.UtcNow;
            try
            {
                if (File.Exists(ConfigFile))
                    foreach (var raw in File.ReadAllLines(ConfigFile))
                    {
                        var line = raw.Trim(); int i = line.IndexOf('=');
                        if (i <= 0 || line.StartsWith("#")) continue;
                        var k = line.Substring(0, i).Trim(); var v = line.Substring(i + 1).Trim();
                        if (k.Equals("SpotifyClientId", StringComparison.OrdinalIgnoreCase)) ClientId = v;
                        else if (k.Equals("SpotifyClientSecret", StringComparison.OrdinalIgnoreCase)) ClientSecret = v;
                    }
                if (File.Exists(TokenFile)) { var t = File.ReadAllText(TokenFile).Trim(); if (t.Length > 0) RefreshToken = t; }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret) || string.IsNullOrWhiteSpace(RefreshToken)) return false;
            try { await RefreshAccess(); SpotifyConnected = true; Log("Spotify reconnected - picked up new keys/token without a restart."); return true; }
            catch (Exception e) { Log("Spotify reconnect attempt failed: " + e.Message); return false; }
        }
        finally { _reconnLock.Release(); }
    }

    static async Task<(string status, string track, string uri)> QueueSpotify(string input)
    {
        if (!SpotifyConnected && !await TryReconnectSpotify()) { Log("  Spotify request but Spotify isn't connected - refunding."); return ("no spotify", null, null); }
        if (!SpotifyEnabled) { Log("  Spotify request ignored - Spotify requests are turned off in the dock/app."); return ("spotify off", null, null); }
        string uri = ExtractTrackUri(input), label = null, artist = null; bool exp = false;
        if (uri == null) { var t = await SearchTrack(input); uri = t.uri; label = t.label; artist = t.artist; exp = t.exp; }
        else { var t = await SpotifyMeta(uri); label = t.label; artist = t.artist; exp = t.exp; }
        if (uri == null) { Log("  No track found for: " + input); return ("not found", null, null); }
        if (BlockExplicit && exp) { Log("  Rejected (explicit): " + (label ?? uri)); return ("explicit", label, uri); }
        if (IsArtistBlocked(artist)) { Log("  Rejected (artist blocked): " + (label ?? uri)); return ("artist blocked", label, uri); }
        if (IsSongBlocked(uri, label)) { Log("  Rejected (song blocked): " + (label ?? uri)); return ("song blocked", label, uri); }
        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.spotify.com/v1/me/player/queue?uri=" + Uri.EscapeDataString(uri));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
        var resp = await Http.SendAsync(req);
        if (resp.IsSuccessStatusCode) { Log("  QUEUED: " + (label ?? uri)); return ("queued", label, uri); }
        int sc = (int)resp.StatusCode;
        if (sc == 404) { Log("  Failed: no active Spotify device (open Spotify and press play)."); return ("no device", label, uri); }
        Log("  Failed (" + sc + "): " + await resp.Content.ReadAsStringAsync());
        return ("error", label, uri);
    }

    // Track name + primary artist + explicit flag for a known uri (used for the blocklist/explicit gates).
    static async Task<(string label, string artist, bool exp)> SpotifyMeta(string uri)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/tracks/" + uri.Replace("spotify:track:", ""));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (null, null, false);
            var t = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            string name = t.GetProperty("name").GetString();
            string artist = t.GetProperty("artists")[0].GetProperty("name").GetString();
            bool exp = t.TryGetProperty("explicit", out var ex) && ex.ValueKind == JsonValueKind.True;
            return (name + " - " + artist, artist, exp);
        }
        catch { return (null, null, false); }
    }

    static string ExtractTrackUri(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"spotify:track:([A-Za-z0-9]+)"); if (m.Success) return "spotify:track:" + m.Groups[1].Value;
        m = Regex.Match(s, @"open\.spotify\.com/track/([A-Za-z0-9]+)"); if (m.Success) return "spotify:track:" + m.Groups[1].Value;
        return null;
    }

    static async Task<(string uri, string label, string artist, bool exp)> SearchTrack(string q)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/search?type=track&limit=1&q=" + Uri.EscapeDataString(q));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
        var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return (null, null, null, false);
        var items = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("tracks").GetProperty("items");
        if (items.GetArrayLength() == 0) return (null, null, null, false);
        var t = items[0];
        string name = t.GetProperty("name").GetString();
        string artist = t.GetProperty("artists")[0].GetProperty("name").GetString();
        bool exp = t.TryGetProperty("explicit", out var ex) && ex.ValueKind == JsonValueKind.True;
        return (t.GetProperty("uri").GetString(), name + " - " + artist, artist, exp);
    }

    static bool _loggedRaw = false;
    static async Task RunLoop()
    {
        while (true)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                Log("Connecting to Streamer.bot at " + WsUrl + " ...");
                await ws.ConnectAsync(new Uri(WsUrl), CancellationToken.None);
                Log("Connected. Listening for '" + RewardName + "' redemptions.");
                await ws.SendAsync(Encoding.UTF8.GetBytes("{\"request\":\"Subscribe\",\"id\":\"sq\",\"events\":{\"Twitch\":[\"RewardRedemption\"]}}"),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                _sbWs = ws;   // now safe to DoAction on this socket
                _ = ApplyRewardState(true);   // force-resync the reward's paused/enabled state on (re)connect
                var buf = new byte[16384]; var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                    if (sb.Length > 4_000_000) { sb.Clear(); continue; }
                    if (!res.EndOfMessage) continue;
                    var msg = sb.ToString(); sb.Clear();
                    await HandleMessage(msg);
                }
            }
            catch (Exception e) { Log("Connection error: " + e.Message); }
            finally { _sbWs = null; }
            Log("Disconnected. Reconnecting in 5s...");
            await Task.Delay(5000);
        }
    }

    static async Task HandleMessage(string msg)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(msg).RootElement; } catch { return; }
        if (root.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.String && rid.GetString() is string sid && sid.StartsWith("sq"))
        {
            Log("  Streamer.bot reply (" + sid + "): " + (msg.Length > 240 ? msg.Substring(0, 240) : msg));   // DoAction response (sqann/sqrefund/sqreward)
            // A missing action fails SOFT (SB just replies with an error) - say plainly what to do about it.
            if (msg.Contains("was not found"))
                Log(sid == "sqann"
                    ? "  ^ Chat announces can't post: import the \"Song Requests Announce\" action (app -> Setup -> chat-reply import), or turn announcements off in the dock."
                    : sid == "sqrefund"
                    ? "  ^ Refunds can't run: the refund action is missing in Streamer.bot (make the reward IN Streamer.bot so it can refund, or ignore this if you refund by hand)."
                    : sid == "sqreward"
                    ? "  ^ Reward auto-pause can't run: no \"" + RewardOnAction + "\"/\"" + RewardOffAction + "\" actions exist in Streamer.bot. Harmless - the reward just stays as-is when request lanes go on/off."
                    : "");
            return;
        }
        if (!root.TryGetProperty("event", out var ev)) return;
        string type = ev.TryGetProperty("type", out var t) ? t.GetString() : "";
        if (!string.Equals(type, "RewardRedemption", StringComparison.OrdinalIgnoreCase)) return;
        if (RedemptionSource == "twitch") return;   // native EventSub owns redemptions in app/twitch mode - don't double-process the SB copy
        if (!root.TryGetProperty("data", out var data)) return;
        if (!_loggedRaw) { _loggedRaw = true; Log("First redemption raw data: " + data.GetRawText()); }

        string reward = FirstString(data, "reward_name", "rewardName") ?? Nested(data, "reward", "title", "name", "Title");
        string input  = FirstString(data, "user_input", "userInput", "input", "message");
        string user   = FirstString(data, "user_name", "userName", "user", "display_name") ?? "someone";
        string redemptionId = FirstString(data, "id", "redemptionId", "redemption_id");
        string rewardId = Nested(data, "reward", "id", "Id") ?? FirstString(data, "reward_id", "rewardId");
        if (!string.IsNullOrEmpty(RewardName) && !string.Equals(reward ?? "", RewardName, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(input)) { Log(user + " redeemed but sent no text."); return; }
        Log(user + " requested: " + input);
        var r = await QueueSong(input.Trim(), user);
        AddRequest(user, input.Trim(), r.track, r.uri, r.status);
        string chat = AnnounceMessage(user, r.status, r.track);   // what happened, for chat (if announcing is on)
        if (r.status != "queued")   // failed request -> refund the points, and note the refund in the chat line
        {
            bool refunding = RefundFailed && !string.IsNullOrEmpty(redemptionId);
            if (refunding) _ = RefundRedemption(redemptionId, rewardId);
            if (chat != null && refunding) chat += " (points refunded)";
        }
        _ = AnnounceToChat(chat);
    }

    // Build the chat line for a redemption result, or null to stay silent for that status.
    static string AnnounceMessage(string user, string status, string track)
    {
        string at = string.IsNullOrEmpty(user) ? "" : "@" + user + " ";
        switch (status)
        {
            case "queued":      return at + "✅ queued: " + (string.IsNullOrEmpty(track) ? "your song" : track);
            case "not found":   return at + "❌ couldn't find that one.";
            case "duplicate":   return at + "that's already in the queue.";
            case "too long":    return at + "that song's over the length limit.";
            case "too short":   return at + "that one's too short.";
            case "no live":     return at + "can't queue a live stream.";
            case "age blocked": return at + "that video is age-restricted.";
            case "no device":   return at + "open Spotify and press play first, then re-request.";
            case "requests off": return at + "song requests are off right now.";
            case "no spotify":  return at + "Spotify isn't connected - send a YouTube link or 'yt <song>' instead.";
            case "spotify off": return at + "Spotify requests are off - try 'yt <song>' for YouTube.";
            case "youtube off": return at + "YouTube requests are off right now.";
            case "user blocked":  return at + "you're blocked from song requests.";
            case "cooldown":      return at + "slow down a sec — song requests are on cooldown.";
            case "queue full":    return at + "the queue is full right now — try again soon.";
            case "user limit":    return at + "you already have the max songs in the queue.";
            case "song blocked":  return at + "that song is blocked.";
            case "artist blocked": return at + "that artist is blocked.";
            case "explicit":      return at + "explicit tracks aren't allowed here.";
            case "error":       return at + "something went wrong adding that.";
            default:            return null;   // no embed / async states etc. -> silent
        }
    }

    // Ask Streamer.bot to post the message to Twitch chat, via DoAction on the socket we already hold.
    // We never touch Twitch directly; SB owns that connection. Fails silently if disabled/disconnected.
    // Run a Streamer.bot action by name with an args object (argsJson = the INSIDE of {...}, or ""), over the
    // socket we already hold. Returns false if not connected. We never touch Twitch directly - SB owns that.
    static async Task<bool> SbDoAction(string actionName, string argsJson, string reqId)
    {
        var ws = _sbWs;
        if (ws == null || ws.State != WebSocketState.Open) return false;
        string json = "{\"request\":\"DoAction\",\"action\":{\"name\":\"" + JEsc(actionName) + "\"},\"args\":{" + argsJson + "},\"id\":\"" + reqId + "\"}";
        try
        {
            await _wsSend.WaitAsync();
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _wsSend.Release(); }
            return true;
        }
        catch (Exception e) { Log("  Streamer.bot DoAction failed: " + e.Message); return false; }
    }

    static async Task AnnounceToChat(string message)
    {
        if (!AnnounceChat || string.IsNullOrWhiteSpace(message)) return;
        if (message.Length > 400)
        {
            int n = 397;
            if (char.IsHighSurrogate(message[n - 1])) n--;   // don't cut an emoji's surrogate pair in half
            message = message.Substring(0, n) + "…";         // stay under Twitch's chat limit
        }
        if (await SbDoAction(AnnounceAction, "\"message\":\"" + JEsc(message) + "\"", "sqann"))
            Log("  Chat announce sent to Streamer.bot (action \"" + AnnounceAction + "\"): " + message);
        else Log("  Chat announce skipped: not connected to Streamer.bot.");
    }

    // Refund a failed request by cancelling its redemption (returns the viewer's points). SB's Update
    // Redemption Status reads the redemptionId/rewardId args we pass. Requires an SB-created reward.
    static async Task RefundRedemption(string redemptionId, string rewardId)
    {
        if (!RefundFailed || string.IsNullOrEmpty(redemptionId)) return;
        string args = "\"redemptionId\":\"" + JEsc(redemptionId) + "\",\"rewardId\":\"" + JEsc(rewardId ?? "") + "\"";
        if (await SbDoAction(RefundAction, args, "sqrefund"))
            Log("  Refund sent to Streamer.bot (action \"" + RefundAction + "\") for redemption " + redemptionId);
    }

    // Enable/pause the channel-point reward to match whether either request lane is on. Only pushes to SB
    // when the desired state actually changes (or force), so we don't spam DoActions.
    static async Task ApplyRewardState(bool force = false)
    {
        if (!PauseReward) return;
        await _rewardLock.WaitAsync();   // serialize so overlapping callers can't desync _rewardApplied from SB
        try
        {
            bool on; lock (_yt) on = RequestsEnabled && ((SpotifyConnected && SpotifyEnabled) || YtEnabled);   // read state INSIDE the lock; a usable lane = connected+on Spotify, or YouTube
            int want = on ? 1 : 0;
            if (!force && _rewardApplied == want) return;
            if (await SbDoAction(on ? RewardOnAction : RewardOffAction, "", "sqreward"))
            { _rewardApplied = want; Log("  Reward " + (on ? "enabled" : "paused") + " via Streamer.bot."); }
        }
        finally { _rewardLock.Release(); }
    }

    static void AddRequest(string user, string input, string track, string uri, string status)
    {
        var r = new Req { time = Now(), user = user, input = input, track = track, uri = uri, status = status, played = "" };
        // Pending drives Spotify played-detection only; YouTube items are marked by the player itself.
        lock (_rl) { Reqs.Add(r); if (status == "queued" && !string.IsNullOrEmpty(uri) && uri.StartsWith("spotify:")) Pending.Add(r); SaveRequests(); }
    }

    static void LoadRequests()
    {
        try { if (File.Exists(RequestsFile)) { var l = JsonSerializer.Deserialize<List<Req>>(File.ReadAllText(RequestsFile)); if (l != null) Reqs.AddRange(l); } } catch { }
        bool changed = false;
        foreach (var r in Reqs)
            if (r.status == "queued" && string.IsNullOrEmpty(r.played)) { r.status = "scammed"; changed = true; }
        if (changed) SaveRequests();
    }
    static void SaveRequests() { try { File.WriteAllText(RequestsFile, JsonSerializer.Serialize(Reqs)); } catch { } }

    // ---- queue governance helpers -----------------------------------------------------------------
    static List<string> ReadLines(string path)
    {
        var list = new List<string>();
        try { if (File.Exists(path)) foreach (var l in File.ReadAllLines(path)) { var t = l.Trim(); if (t.Length > 0 && !t.StartsWith("#")) list.Add(t); } } catch { }
        return list;
    }
    static void LoadBlacklists()
    {
        var u = new HashSet<string>(ReadLines(UserBlFile), StringComparer.OrdinalIgnoreCase);
        var s = ReadLines(SongBlFile); var a = ReadLines(ArtistBlFile);
        UserBlacklist = u; SongBlacklist = s; ArtistBlacklist = a;   // swap whole refs (readers hold a snapshot, no lock needed)
    }
    // add | remove | set (set: val is newline-joined). Rewrites the file and reloads. Returns false on a bad kind/op.
    static bool BlacklistEdit(string kind, string op, string val)
    {
        string file = kind == "user" ? UserBlFile : kind == "song" ? SongBlFile : kind == "artist" ? ArtistBlFile : null;
        if (file == null) return false;
        val = val ?? "";
        lock (_blLock)
        {
            var list = ReadLines(file);
            if (op == "add")
            {
                var v = val.Trim(); if (v.Length == 0) return false;
                bool exists = false; foreach (var x in list) if (x.Equals(v, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (!exists) list.Add(v);
            }
            else if (op == "remove") list.RemoveAll(x => x.Equals(val.Trim(), StringComparison.OrdinalIgnoreCase));
            else if (op == "set") { list = new List<string>(); foreach (var part in val.Split('\n')) { var t = part.Trim(); if (t.Length > 0) list.Add(t); } }
            else return false;
            try { File.WriteAllLines(file, list); } catch { }
        }
        LoadBlacklists();
        Log("Blocklist " + kind + " " + op + (op == "set" ? "" : ": " + val.Trim()));
        return true;
    }
    static bool IsUserBlocked(string user)
        => !string.IsNullOrEmpty(user) && UserBlacklist.Contains(user.Trim());
    static bool IsArtistBlocked(string artist)
    {
        if (string.IsNullOrEmpty(artist)) return false;
        var a = artist.ToLowerInvariant();
        foreach (var raw in ArtistBlacklist) { var t = raw.Trim().ToLowerInvariant(); if (t.Length > 0 && (a == t || a.Contains(t))) return true; }
        return false;
    }
    static bool IsSongBlocked(string uri, string label)
    {
        var songs = SongBlacklist; if (songs.Count == 0) return false;
        string lab = (label ?? "").ToLowerInvariant();
        string spUri = uri != null && uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? uri : null;
        string ytId = uri != null && uri.StartsWith("youtube:video:", StringComparison.Ordinal) ? uri.Substring("youtube:video:".Length) : null;
        foreach (var raw in songs)
        {
            var term = raw.Trim(); if (term.Length == 0) continue;
            if (spUri != null) { var spTerm = ExtractTrackUri(term); if (spTerm != null && spTerm == spUri) return true; }
            if (ytId != null) { var ytTerm = YouTube.ExtractId(term); if (ytTerm != null && ytTerm == ytId) return true; }
            if (lab.Length > 0 && lab.Contains(term.ToLowerInvariant())) return true;
        }
        return false;
    }
    static int CountOutstanding()
    { lock (_rl) { int n = 0; foreach (var r in Reqs) if (r.status == "queued") n++; return n; } }
    static int CountOutstandingForUser(string user)
    { lock (_rl) { int n = 0; foreach (var r in Reqs) if (r.status == "queued" && string.Equals(r.user, user, StringComparison.OrdinalIgnoreCase)) n++; return n; } }
    // Viewer-facing pre-checks (not applied to the streamer's own dock add). Returns a rejection status, or null to allow.
    static string CheckGate(string user)
    {
        if (IsUserBlocked(user)) return "user blocked";
        var now = DateTime.UtcNow;
        lock (_gateLock)
        {
            if (SrCooldownSec > 0 && (now - _lastReqUtc).TotalSeconds < SrCooldownSec) return "cooldown";
            if (SrPerUserCooldownSec > 0 && !string.IsNullOrEmpty(user)
                && _lastReqByUser.TryGetValue(user, out var last) && (now - last).TotalSeconds < SrPerUserCooldownSec) return "cooldown";
        }
        if (MaxQueueLength > 0 && CountOutstanding() >= MaxQueueLength) return "queue full";
        if (MaxRequestsPerUser > 0 && !string.IsNullOrEmpty(user) && CountOutstandingForUser(user) >= MaxRequestsPerUser) return "user limit";
        return null;
    }
    static void MarkRequested(string user)   // called only after a successful viewer request, to start the cooldown(s)
    { var now = DateTime.UtcNow; lock (_gateLock) { _lastReqUtc = now; if (!string.IsNullOrEmpty(user)) _lastReqByUser[user] = now; } }
    static void AppendJsonArray(StringBuilder sb, string name, IEnumerable<string> items)
    {
        sb.Append(",\"").Append(name).Append("\":[");
        bool first = true; foreach (var it in items) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(JEsc(it)).Append('"'); }
        sb.Append(']');
    }

    // Skip Spotify songs the streamer marked "skip when it plays" almost the instant they start, instead of
    // waiting on the 2.5s now-playing poll (which let ~3s of the song leak through). Cheap: it only fast-polls
    // in the last few seconds before a marked song is due, or right after a skip (to chain consecutive marked
    // songs), so it isn't hammering Spotify while marked songs are still far down the queue.
    static async Task SkipWatcher()
    {
        while (true)
        {
            int delay = 900;
            try
            {
                if (SpotifyConnected)
                {
                    bool watch;
                    lock (_yt)
                    {
                        bool nextMarked = _skipUris.Count > 0 && _spotNext.Count > 0
                            && !string.IsNullOrEmpty(_spotNext[0].uri) && _skipUris.Contains(_spotNext[0].uri);
                        double remMs = _spotDurMs - _spotProgMs
                            - (_spotSeenUtc == DateTime.MinValue ? 0 : (DateTime.UtcNow - _spotSeenUtc).TotalMilliseconds);
                        bool nearEnd = _spotPlaying && remMs < 6000;                           // current song about to hand off
                        bool runWindow = (DateTime.UtcNow - _lastMarkedSkip).TotalSeconds < 5; // just skipped -> a chain of marked songs may follow
                        watch = _skipUris.Count > 0 && ((nextMarked && nearEnd) || runWindow);
                    }
                    if (watch)
                    {
                        delay = 400;
                        string cur = await CurrentUri();   // returns the uri only while actually playing (null during a YouTube item)
                        if (cur != null)
                        {
                            bool doSkip; lock (_yt) doSkip = _skipUris.Remove(cur);
                            if (doSkip) { _lastMarkedSkip = DateTime.UtcNow; Log("Auto-skipped (marked to skip): " + cur); await SpotifySkipNext(); }
                        }
                    }
                }
            }
            catch { }
            await Task.Delay(delay);
        }
    }

    static async Task PlayedWatcher()
    {
        while (true)
        {
            try
            {
                await Task.Delay(15000);
                if (!SpotifyConnected) continue;
                bool any; lock (_rl) any = Pending.Count > 0;
                if (!any) continue;
                string cur = await CurrentUri();
                if (cur == null) continue;
                lock (_rl)
                {
                    var r = Pending.Find(x => x.uri == cur);
                    if (r != null) { r.played = Now(); r.status = "played"; Pending.Remove(r); SaveRequests(); }
                }
            }
            catch { }
        }
    }

    static async Task<string> CurrentUri()
    {
        if (!SpotifyConnected) return null;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return null;
            var root = JsonDocument.Parse(body).RootElement;
            // Only "played" when it's actually playing: the engine now pauses Spotify for YouTube, so a
            // paused-at-0:00 track (auto-advanced under a late pause) must not be counted as played.
            if (!(root.TryGetProperty("is_playing", out var ip) && ip.GetBoolean())) return null;
            if (root.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object && it.TryGetProperty("uri", out var u)) return u.GetString();
        }
        catch { }
        return null;
    }

    static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    static void SetNow(string j) { lock (_np) NowJson = j; }
    static string GetNow() { lock (_np) return NowJson; }

    // ---- OBS now-playing file outputs -------------------------------------------------------------
    // Write the current track to plain files that an OBS "Text (GDI+)" source (read-from-file) and an
    // Image source can point at - the classic local now-playing overlay, no browser source required.
    // Called from the Spotify poll and the YouTube player report; debounced so the disk is only touched
    // when the visible line (or the art) actually changes, not on every 2.5s / 0.8s poll.
    static string LookupRequester(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        lock (_rl) { var r = Reqs.FindLast(x => x.uri == uri && (x.status == "queued" || x.status == "played")); return r?.user; }
    }
    static string SpotifyTrackUrl(string uri)
        => string.IsNullOrEmpty(uri) || !uri.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? "" : "https://open.spotify.com/track/" + uri.Substring("spotify:track:".Length);

    static void PublishOutputs(string source, string title, string artist, string artUrl, string requester, string url, bool playing)
    {
        if (!OutputEnabled) return;
        try
        {
            string mainLine, artistOut, titleOut, reqOut, coverUrl;
            if (!playing && PauseBehavior != "nothing")   // paused/idle: clear the files, or show custom text
            {
                mainLine = PauseBehavior == "text" ? (CustomPauseText ?? "") : "";
                artistOut = ""; titleOut = ""; reqOut = ""; coverUrl = "";
            }
            else if (!playing) return;                    // "nothing" (default): leave the last song shown
            else
            {
                mainLine = FillTokens(OutputFormat, artist, title, requester, url);
                artistOut = artist ?? ""; titleOut = title ?? "";
                reqOut = string.IsNullOrEmpty(requester) ? "" : (RequesterPrefix ?? "") + requester;
                coverUrl = artUrl ?? "";
            }
            string pad = AppendSpaces && SpaceCount > 0 ? new string(' ', SpaceCount) : "";
            if (pad.Length > 0 && mainLine.Length > 0) mainLine += pad;

            string key = mainLine + "" + artistOut + "" + titleOut + "" + reqOut;
            lock (_outLock)
            {
                if (key != _outLastKey)
                {
                    _outLastKey = key;
                    WriteOut("nowplaying.txt", mainLine);
                    if (SplitOutput)
                    {
                        WriteOut("artist.txt", pad.Length > 0 && artistOut.Length > 0 ? artistOut + pad : artistOut);
                        WriteOut("title.txt",  pad.Length > 0 && titleOut.Length  > 0 ? titleOut  + pad : titleOut);
                        WriteOut("requester.txt", reqOut);
                    }
                }
                if (DownloadCover && coverUrl != _outLastArtUrl) { _outLastArtUrl = coverUrl; _ = UpdateCover(coverUrl); }
            }
        }
        catch { }
    }

    static void WriteOut(string name, string content)
    { try { File.WriteAllText(Path.Combine(OutputDir, name), content ?? "", new UTF8Encoding(false)); } catch { } }

    // {artist} {single_artist} {title} {req} {requester} {url} {uri}, plus a {{ ... }} block that is only
    // kept when the song was actually requested by someone (so "requested by X" vanishes for autoplay).
    static string FillTokens(string fmt, string artist, string title, string requester, string url)
    {
        if (string.IsNullOrEmpty(fmt)) return "";
        bool hasReq = !string.IsNullOrEmpty(requester);
        // Substitute the value tokens FIRST, so a {req}/{title} that ends a {{ ... }} block can't collide
        // with the block's closing braces (e.g. "{{ requested by {req}}}" -> "}}}" would mis-parse).
        fmt = fmt.Replace("{single_artist}", artist ?? "").Replace("{artist}", artist ?? "")
                 .Replace("{title}", title ?? "").Replace("{requester}", requester ?? "").Replace("{req}", requester ?? "")
                 .Replace("{url}", url ?? "").Replace("{uri}", url ?? "");
        // Then resolve the conditional block: keep its inner text only when the song was actually requested.
        return Regex.Replace(fmt, @"\{\{(.*?)\}\}", m => hasReq ? m.Groups[1].Value : "", RegexOptions.Singleline);
    }

    // Save album/thumbnail art to cover.png (best-effort, atomic). An empty url writes a 1x1 transparent
    // png so an Image source pointed at cover.png doesn't keep showing the last song while nothing plays.
    static readonly SemaphoreSlim _coverLock = new SemaphoreSlim(1, 1);
    static async Task UpdateCover(string url)
    {
        string path = Path.Combine(OutputDir, "cover.png"), tmp = path + ".tmp";
        await _coverLock.WaitAsync();
        try
        {
            byte[] bytes;
            if (string.IsNullOrEmpty(url))
            {
                try { bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="); }
                catch { return; }
            }
            else bytes = await Http.GetByteArrayAsync(url);
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, true);
        }
        catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        finally { _coverLock.Release(); }
    }
    static string JEsc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    static void ReadItem(JsonElement item, out string name, out string artist, out string art, out int dur, bool smallestArt)
    {
        name = item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : "";
        artist = "";
        if (item.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array && arts.GetArrayLength() > 0 && arts[0].TryGetProperty("name", out var an))
            artist = an.GetString();
        else if (item.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object && show.TryGetProperty("name", out var sn))
            artist = sn.GetString();
        art = "";
        JsonElement imgs;
        bool have = (item.TryGetProperty("album", out var alb) && alb.ValueKind == JsonValueKind.Object && alb.TryGetProperty("images", out imgs) && imgs.ValueKind == JsonValueKind.Array)
            || (item.TryGetProperty("images", out imgs) && imgs.ValueKind == JsonValueKind.Array)
            || (item.TryGetProperty("show", out var sh) && sh.ValueKind == JsonValueKind.Object && sh.TryGetProperty("images", out imgs) && imgs.ValueKind == JsonValueKind.Array);
        if (have && imgs.GetArrayLength() > 0)
        {
            int idx = smallestArt ? imgs.GetArrayLength() - 1 : 0;
            if (imgs[idx].TryGetProperty("url", out var iu)) art = iu.GetString();
        }
        dur = item.TryGetProperty("duration_ms", out var dm) && dm.ValueKind == JsonValueKind.Number && dm.TryGetInt32(out var dv) ? dv : 0;
    }

    // Returns the body on a real 2xx, "" only for a genuine empty success (204 = Spotify idle), and
    // null on any failure (429/network/token error) so callers can tell "idle" from "couldn't reach it".
    static async Task<string> GetBody(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
            using var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
        }
        catch { return null; }
    }

    static async Task NowPlayingLoop()
    {
        while (true)
        {
            try
            {
                if (!SpotifyConnected)   // YouTube-only: nothing to poll; keep Spotify state idle so the conductor plays YT straight through
                {
                    // clear _spotUserPaused too - with no Spotify to pause it's meaningless, and if left set it would wedge the YouTube handoff (userHold)
                    bool ytA; lock (_yt) { _spotNext.Clear(); _spotPlaying = false; _spotUserPaused = false; _spotProgMs = 0; _spotDurMs = 0; _spotSeenUtc = DateTime.UtcNow; ytA = YtActive != null; }
                    if (!ytA) { SetNow("{\"playing\":false}"); PublishOutputs("spotify", null, null, null, null, null, false); }
                    await Task.Delay(2500);
                    continue;
                }
                // Spotify's queue -> the dock's up-next list (and the overlay's "next" when nothing YT is waiting)
                try
                {
                    string qBody = await GetBody("https://api.spotify.com/v1/me/player/queue");
                    if (!string.IsNullOrWhiteSpace(qBody))
                    {
                        var q = JsonDocument.Parse(qBody).RootElement;
                        lock (_yt)
                        {
                            _spotNext.Clear();
                            if (q.TryGetProperty("queue", out var qa) && qa.ValueKind == JsonValueKind.Array)
                                for (int i = 0; i < qa.GetArrayLength() && _spotNext.Count < 10; i++)   // show a healthy run of upcoming autoplay (the dock shows them all, incl. marked-to-skip)
                                {
                                    ReadItem(qa[i], out string nT, out string nA, out string nArt, out _, true);
                                    string nUri = qa[i].TryGetProperty("uri", out var nu) && nu.ValueKind == JsonValueKind.String ? nu.GetString() : "";
                                    if (nT.Length > 0) _spotNext.Add((nT, nA, nArt, nUri));
                                }
                        }
                    }
                }
                catch { }

                string body = await GetBody("https://api.spotify.com/v1/me/player/currently-playing");
                bool ytActive; lock (_yt) ytActive = YtActive != null;
                if (body == null)
                {
                    // Couldn't reach Spotify (429/network/token) - keep the last known state so the
                    // conductor keeps waiting instead of mistaking a blip for "Spotify is idle".
                }
                else if (string.IsNullOrWhiteSpace(body))
                {
                    lock (_yt) { _spotPlaying = false; _spotProgMs = 0; _spotDurMs = 0; _spotSeenUtc = DateTime.UtcNow; }
                    if (!ytActive) { SetNow("{\"playing\":false}"); PublishOutputs("spotify", null, null, null, null, null, false); }
                }
                else
                {
                    var root = JsonDocument.Parse(body).RootElement;
                    bool isPlaying = root.TryGetProperty("is_playing", out var ip) && ip.GetBoolean();
                    if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                    {
                        lock (_yt) { _spotPlaying = false; _spotProgMs = 0; _spotDurMs = 0; _spotSeenUtc = DateTime.UtcNow; }
                        if (!ytActive) { SetNow("{\"playing\":false}"); PublishOutputs("spotify", null, null, null, null, null, false); }
                    }
                    else
                    {
                        ReadItem(item, out string name, out string artist, out string art, out int dur, false);
                        string curUri = item.TryGetProperty("uri", out var cu) && cu.ValueKind == JsonValueKind.String ? cu.GetString() : null;
                        bool skipThis; lock (_yt) skipThis = isPlaying && curUri != null && _skipUris.Remove(curUri);   // marked-to-skip and now playing
                        if (skipThis) { Log("Auto-skipping (removed from queue): " + name); _ = SpotifySkipNext(); }
                        int prog = root.TryGetProperty("progress_ms", out var pm) && pm.ValueKind == JsonValueKind.Number && pm.TryGetInt32(out var pv) ? pv : 0;
                        lock (_yt) { _spotPlaying = isPlaying; if (isPlaying) _spotUserPaused = false; _spotProgMs = prog; _spotDurMs = dur; _spotSeenUtc = DateTime.UtcNow; }   // feeds the YT handoff
                        if (!ytActive)   // while a YouTube item plays, the overlay player owns the feed
                        {
                            SetNow("{\"playing\":" + (isPlaying ? "true" : "false") + ",\"source\":\"spotify\",\"title\":\"" + JEsc(name) + "\",\"artist\":\"" + JEsc(artist) + "\",\"art\":\"" + JEsc(art) + "\",\"progress\":" + prog + ",\"duration\":" + dur + NextJson() + "}");
                            PublishOutputs("spotify", name, artist, art, LookupRequester(curUri), SpotifyTrackUrl(curUri), isPlaying);
                        }
                    }
                }
            }
            catch { }
            await Task.Delay(2500);
        }
    }

    static void NowServer()
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add("http://127.0.0.1:" + NpPort + "/");
            l.Start();
            Log("Now-playing feed on http://127.0.0.1:" + NpPort + "/nowplaying");
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); } catch { break; }
                _ = Task.Run(() => Serve(ctx));   // concurrent: a slow /cmd resolve must not block overlay polls
            }
        }
        catch (Exception e) { Log("Now-playing server error: " + e.Message); }
    }

    static async Task Serve(HttpListenerContext ctx)
    {
        string body = "{}", type = "application/json", acao = null;
        try
        {
            string path = (ctx.Request.Url.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
            var q = ctx.Request.QueryString;
            string origin = ctx.Request.Headers["Origin"];
            bool localOrigin = string.IsNullOrEmpty(origin) || origin == "null" || OriginIsLoopback(origin);
            bool okOrigin = localOrigin;   // only loopback / local-file pages may drive the engine (no external origin)

            // CORS preflight (incl. Private Network Access for a local file:// page reaching loopback).
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                if (okOrigin && !string.IsNullOrEmpty(origin))
                {
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", origin);
                    ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    ctx.Response.AddHeader("Access-Control-Allow-Private-Network", "true");
                    ctx.Response.AddHeader("Access-Control-Max-Age", "86400");
                }
                ctx.Response.StatusCode = 204; ctx.Response.Close(); return;
            }

            if (path == "" || path == "/nowplaying") { body = GetNow(); acao = "*"; }            // public read (already on-stream)
            else if (path == "/dock") { body = RootFile("dock.html"); type = "text/html; charset=utf-8"; }
            else if (path == "/youtube") { body = RootFile("youtube.html"); type = "text/html; charset=utf-8"; }
            else if (path == "/token")
            {
                // Bootstrap the control token for a local page loaded via file:// or http loopback;
                // refused to any external website so a drive-by page can't grab it.
                if (okOrigin) { body = "{\"tok\":\"" + CtlToken + "\"}"; acao = origin ?? "*"; }
                else ctx.Response.StatusCode = 403;
            }
            // Control/state endpoints require the per-run token (blocks any random site the streamer visits).
            // A local page (dock/player served over http OR loaded via file://) may hold a stale token
            // after the engine restarted - give it CORS on the 403 so it can read it and re-fetch /token.
            else if (!string.Equals(q["tok"], CtlToken, StringComparison.Ordinal) || string.IsNullOrEmpty(CtlToken))
                { ctx.Response.StatusCode = 403; if (okOrigin) acao = origin ?? "*"; }
            else if (path == "/ytaudio") { await ServeAudio(ctx, q["id"], origin); return; }   // binary audio (writes + closes its own response)
            else if (path == "/player") { body = HandlePlayer(q); acao = origin; }
            else if (path == "/queue") { body = BuildQueueJson(); acao = origin; }
            else if (path == "/cmd") { body = await HandleCmd(q); acao = origin; }
            else ctx.Response.StatusCode = 404;
        }
        catch { try { ctx.Response.StatusCode = 500; } catch { } }
        try
        {
            var buf = Encoding.UTF8.GetBytes(body);
            if (!string.IsNullOrEmpty(acao)) ctx.Response.AddHeader("Access-Control-Allow-Origin", acao);
            ctx.Response.AddHeader("Referrer-Policy", "strict-origin-when-cross-origin");   // so the YouTube embed sends a Referer (else error 153)
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        }
        catch { }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    static bool OriginIsLoopback(string origin)
    { try { var u = new Uri(origin); return u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]"; } catch { return false; } }

    // Root assets (dock.html etc.) live in the install root; the engine runs from <root>\app.
    static string RootFile(string name)
    {
        try
        {
            string root = Environment.GetEnvironmentVariable("SQ_ROOT");
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                string b = AppContext.BaseDirectory.TrimEnd('\\');
                root = string.Equals(Path.GetFileName(b), "app", StringComparison.OrdinalIgnoreCase)
                    ? Directory.GetParent(b)?.FullName ?? b : b;
            }
            string f = Path.Combine(root, name);
            if (File.Exists(f)) return File.ReadAllText(f).Replace("__SQTOKEN__", CtlToken);   // inject control token
        }
        catch { }
        return "<html><body style='background:#111;color:#ccc;font-family:sans-serif'>File not found.</body></html>";
    }

    // Streams a cached YouTube audio file to the overlay's <audio> element. Supports HTTP Range so the
    // browser can seek/buffer normally. This is what replaces the YouTube embed - a plain local media file.
    static async Task ServeAudio(HttpListenerContext ctx, string id, string origin)
    {
        try
        {
            string file = YouTube.CachedPath(id);
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string ct = ext == ".mp4" || ext == ".m4v" ? "video/mp4" : ext == ".mkv" ? "video/x-matroska"   // progressive video files (audio-only is .m4a/.webm/.opus)
                      : ext == ".webm" ? "audio/webm" : ext == ".opus" || ext == ".ogg" ? "audio/ogg"
                      : ext == ".mp3" ? "audio/mpeg" : "audio/mp4";
            // FileShare.Delete so DeleteCached (File.Delete on 'ended'/skip/remove) can unlink the file
            // even while this stream is still in flight, instead of throwing and leaking the file.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long len = fs.Length, start = 0, end = len - 1;
            bool partial = false;
            string range = ctx.Request.Headers["Range"];
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = range.Substring(6).Split('-');
                long e2 = 0;
                bool hasStart = long.TryParse(spec[0], out var s);
                bool hasEnd = spec.Length > 1 && long.TryParse(spec[1], out e2);
                if (hasStart) { start = s; partial = true; if (hasEnd) end = e2; }
                else if (hasEnd) { partial = true; start = len - e2; end = len - 1; }   // suffix form "bytes=-N" -> the last N bytes
                if (start < 0) start = 0;
                if (end >= len) end = len - 1;
                if (start > end) { ctx.Response.AddHeader("Content-Range", "bytes */" + len); ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }
            }
            if (!string.IsNullOrEmpty(origin)) ctx.Response.AddHeader("Access-Control-Allow-Origin", origin);
            ctx.Response.AddHeader("Accept-Ranges", "bytes");
            ctx.Response.ContentType = ct;
            long count = end - start + 1;
            if (partial) { ctx.Response.StatusCode = 206; ctx.Response.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + len); }
            ctx.Response.ContentLength64 = count;
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[81920]; long remaining = count;
            while (remaining > 0)
            {
                int n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (n <= 0) break;
                await ctx.Response.OutputStream.WriteAsync(buf, 0, n);
                remaining -= n;
            }
        }
        catch { try { ctx.Response.StatusCode = 500; } catch { } }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    // ---- YouTube conductor: hands playback between Spotify (the bed) and the overlay player ----
    static async Task YtConductor()
    {
        while (true)
        {
            try
            {
                await Task.Delay(500);
                // Keep running even when YouTube is disabled so an in-flight item still finishes and Spotify
                // still resumes; disabling only blocks NEW requests (QueueYouTube) and starting queued items (below).
                bool doPause = false, doResume = false; YtItem started = null; string backstop = null;
                YtItem needDl = null, dropFailed = null;
                lock (_yt)
                {
                    bool alive = (DateTime.UtcNow - _lastPlayerSeen).TotalSeconds <= 8;
                    if (YtActive != null)
                    {
                        if (!alive) backstop = "player gone";                                        // OBS source closed mid-item
                        else if (!_ytPaused && (DateTime.UtcNow - _ytStarted).TotalSeconds > MaxSongSec + 20) backstop = "played";  // hard cap (frozen while dock-paused)
                    }
                    if (YtQ.Count == 0) _skipToYt = false;   // nothing to skip to anymore
                    // Owed a Spotify resume but nothing left that will consume it (player died with items
                    // still queued, or the queue drained while the player was gone/idle) -> resume now.
                    if (YtActive == null && _resumeSpotify && (!alive || YtQ.Count == 0 || !YtEnabled))
                    { _resumeSpotify = false; doResume = true; }

                    if (backstop == null && YtActive == null && YtQ.Count > 0 && alive && YtEnabled)   // don't START items while disabled
                    {
                        // Remaining time on the current Spotify track, extrapolated from the last poll so
                        // poll latency can't straddle the handoff window. Start immediately once Spotify is
                        // idle or we're already mid-YouTube-run (_resumeSpotify owed).
                        double remMs = _spotDurMs - _spotProgMs
                            - (_spotSeenUtc == DateTime.MinValue ? 0 : (DateTime.UtcNow - _spotSeenUtc).TotalMilliseconds);
                        bool spotifyBusy = _spotPlaying && !_resumeSpotify && remMs > 3500;
                        bool userHold = _spotUserPaused && !_skipToYt;   // streamer paused Spotify on purpose -> don't auto-hand-off (Skip still forces it)
                        if ((!spotifyBusy || _skipToYt) && !userHold)   // Skip button forces the waiting YouTube item to play now
                        {
                            bool forced = _skipToYt;   // this handoff was forced by Skip (the user skipped the Spotify song)
                            var front = YtQ[0];
                            if (front.dlFailed) { dropFailed = front; YtQ.RemoveAt(0); }   // audio couldn't be fetched -> drop, leave Spotify playing
                            else if (front.file == null) { needDl = front; }               // still downloading -> keep waiting; Spotify keeps playing (no dead air)
                            else
                            {
                                _skipToYt = false; _spotUserPaused = false;
                                started = front; YtQ.RemoveAt(0);
                                YtActive = started; _ytStarted = DateTime.UtcNow; YtEpoch++; _ytPaused = false;
                                // re-pause even mid-run if the user resumed Spotify; if this handoff was a Skip,
                                // remember to advance Spotify past the skipped song (don't replay it) on resume.
                                if (_spotPlaying) { _resumeSpotify = true; doPause = true; if (forced) _spotSkipOnResume = true; }
                            }
                        }
                    }
                }
                if (dropFailed != null) { MarkYtReq(dropFailed.id, "won't play"); YouTube.DeleteCached(dropFailed.id); Log("YouTube won't play (couldn't fetch audio): " + dropFailed.title); }
                if (needDl != null) _ = EnsureMedia(needDl);   // backstop: fetch it if the queue-time prefetch didn't run
                if (backstop != null) await FinishActive(backstop);
                if (doPause) { WriteResumeMarker(true); await SpotifyPlayback(false); lock (_yt) _spotPlaying = false; }     // optimistic state so the next handoff is accurate before the poll catches up
                if (doResume) { WriteResumeMarker(false); await ResumeSpotify(); }
                if (started != null) Log("YouTube playing: " + started.title + (string.IsNullOrEmpty(started.user) ? "" : "  (requested by " + started.user + ")"));
            }
            catch { }
        }
    }

    // Finish the active YouTube item (ended / error / skipped / backstop) and resume Spotify when
    // nothing else is queued. expectId/expectEpoch guard against stale reports from the overlay.
    static async Task FinishActive(string status, string expectId = null, long expectEpoch = -1)
    {
        YtItem it; bool resume;
        lock (_yt)
        {
            if (YtActive == null) return;
            if (expectId != null && !string.Equals(expectId, YtActive.id, StringComparison.Ordinal)) return;
            if (expectEpoch >= 0 && expectEpoch != YtEpoch) return;
            it = YtActive; YtActive = null; YtEpoch++; _ytPaused = false;
            // Resume Spotify between items whenever the next thing isn't ready to play instantly (queue
            // empty, or the next item's audio is still downloading) - otherwise Spotify sits paused in
            // dead air until that download finishes. The conductor re-pauses when the next audio is ready.
            resume = _resumeSpotify && (!YtEnabled || YtQ.Count == 0 || YtQ[0].file == null);
            if (resume) _resumeSpotify = false;
        }
        MarkYtReq(it.id, status);
        YouTube.DeleteCached(it.id);   // free the cached audio file
        Log("YouTube " + status + ": " + it.title);
        if (resume) { WriteResumeMarker(false); await ResumeSpotify(); }
    }

    // Owed-resume marker: written when we pause Spotify for YouTube, cleared when we resume it. If the
    // engine is killed/crashes mid-item, the next start reads it and resumes Spotify (no stuck silence).
    static void WriteResumeMarker(bool owed)
    {
        try { if (owed) File.WriteAllText(ResumeMarker, "1"); else if (File.Exists(ResumeMarker)) File.Delete(ResumeMarker); }
        catch { }
    }

    // Runs on a clean stop (GUI Stop / --stop / update) before the process exits: resume Spotify if we
    // owe it, and don't leave the in-flight YouTube item looking un-played.
    static void GracefulStop()
    {
        try
        {
            YtItem it; bool resume;
            lock (_yt) { it = YtActive; YtActive = null; YtEpoch++; resume = _resumeSpotify; _resumeSpotify = false; }
            if (it != null) MarkYtReq(it.id, "played");
            if (resume) { WriteResumeMarker(false); try { SpotifyPlayback(true).Wait(3000); } catch { } }
            if (PauseReward && !_restarting) { try { SbDoAction(RewardOffAction, "", "sqreward").Wait(1500); } catch { } }   // pause on a real stop; on Restart the relaunch resyncs it
        }
        catch { }
    }

    static void MarkYtReq(string id, string status)
    {
        string uri = "youtube:video:" + id;
        lock (_rl)
        {
            var r = Reqs.FindLast(x => x.uri == uri && x.status == "queued");
            if (r == null) return;
            r.status = status;
            if (status == "played") r.played = Now();
            SaveRequests();
        }
    }

    static async Task SpotifyPlayback(bool play)
    {
        if (!SpotifyConnected) return;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Put, "https://api.spotify.com/v1/me/player/" + (play ? "play" : "pause"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
            req.Content = new StringContent("");
            using var resp = await Http.SendAsync(req);
        }
        catch { }
    }

    static async Task SpotifySkipNext()
    {
        if (!SpotifyConnected) return;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.spotify.com/v1/me/player/next");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
            using var resp = await Http.SendAsync(req);
        }
        catch { }
    }

    static async Task SpotifySeek(int ms)
    {
        if (!SpotifyConnected) return;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Put, "https://api.spotify.com/v1/me/player/seek?position_ms=" + ms);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
            req.Content = new StringContent("");
            using var resp = await Http.SendAsync(req);
        }
        catch { }
    }

    // Resume the Spotify "bed" after a YouTube run. If the user reached the YouTube item by SKIPPING the
    // Spotify song (not by it ending / YouTube playing at a boundary), advance past that song so it isn't
    // replayed from where it was paused - the skip should move the whole queue forward.
    static async Task ResumeSpotify()
    {
        if (!SpotifyConnected) return;
        bool skip; lock (_yt) { skip = _spotSkipOnResume; _spotSkipOnResume = false; }
        if (skip) await SpotifySkipNext();   // consume the skipped song -> land on the next track
        await SpotifyPlayback(true);
        lock (_yt) _spotPlaying = true;      // optimistic: don't wait for the next poll to know we're playing
    }

    // The overlay player checks in here (~every 800ms): reports its state, receives its orders.
    // "ready=1" means the audio page is loaded in OBS - only then do we count it alive, so the conductor
    // never pauses Spotify into a source that isn't actually there.
    static string HandlePlayer(System.Collections.Specialized.NameValueCollection q)
    {
        string ev = q["ev"] ?? "poll";
        if (ev != "poll" || q["ready"] == "1") lock (_yt) _lastPlayerSeen = DateTime.UtcNow;
        string id = q["id"]; long ep = long.TryParse(q["epoch"], out var e) ? e : -1;
        if (ev == "ended") _ = FinishActive("played", id, ep);
        else if (ev == "error")
        {
            string code = q["code"] ?? "?";
            // HTML5 <audio> MediaError: 2=network, 3=decode, 4=format/src unsupported; "autoplay" = OBS
            // refused to autostart with sound (needs the app's "Fix YouTube sound in OBS" button once).
            string reason = code == "autoplay" ? "OBS blocked autoplay - click \"Fix YouTube sound in OBS\" in the app once"
                          : code == "4" ? "the audio format wasn't playable"
                          : code == "2" ? "a network error fetching the audio"
                          : "a playback error (" + code + ")";
            Log("YouTube audio can't play " + (string.IsNullOrEmpty(id) ? "" : id + " - ") + reason + ". Skipping.");
            _ = FinishActive("won't play", id, ep);
        }
        YtItem it; long epoch; bool ytPaused; int seekMs; long seekId; bool showCard; int vol;
        lock (_yt) { it = YtActive; epoch = YtEpoch; ytPaused = _ytPaused; seekMs = _ytSeekMs; seekId = _ytSeekId; showCard = YtShowCard; vol = YtVol; }
        if (it == null) return "{\"action\":\"idle\",\"epoch\":" + epoch + ",\"showCard\":" + (showCard ? "true" : "false") + "}";
        if (ev == "poll" && id == it.id && int.TryParse(q["pos"], out var pos))
        {
            int dur = int.TryParse(q["dur"], out var d) && d > 0 ? d : it.durSec * 1000;
            bool ytPlaying = q["playing"] == "1";
            SetNow(BuildYtNow(it, pos, dur, ytPlaying));   // the player drives the feed while YouTube is active
            PublishOutputs("youtube", it.title, it.channel, "https://i.ytimg.com/vi/" + it.id + "/hqdefault.jpg", it.user, "https://youtu.be/" + it.id, ytPlaying);
        }
        return "{\"action\":\"play\",\"videoId\":\"" + it.id + "\",\"epoch\":" + epoch +
               ",\"capMs\":" + (MaxSongSec + 5) * 1000 + ",\"vol\":" + vol + ",\"showCard\":" + (showCard ? "true" : "false") +
               ",\"video\":" + (it.isVideo ? "true" : "false") +
               ",\"paused\":" + (ytPaused ? "true" : "false") + ",\"seekMs\":" + seekMs + ",\"seekId\":" + seekId +
               ",\"title\":\"" + JEsc(it.title) + "\",\"channel\":\"" + JEsc(it.channel) + "\",\"user\":\"" + JEsc(it.user ?? "") + "\"}";
    }

    static string BuildYtNow(YtItem it, int posMs, int durMs, bool playing)
        => "{\"playing\":" + (playing ? "true" : "false") + ",\"source\":\"youtube\",\"title\":\"" + JEsc(it.title) +
           "\",\"artist\":\"" + JEsc(it.channel) + "\",\"art\":\"https://i.ytimg.com/vi/" + it.id + "/hqdefault.jpg\"" +
           ",\"progress\":" + posMs + ",\"duration\":" + durMs + NextJson() + "}";

    // "Up next" across both sources: a waiting YouTube item outranks Spotify's queue (it plays next).
    static string NextJson()
    {
        YtItem n; (string title, string artist, string art) sp = default; bool haveSp = false;
        lock (_yt)
        {
            n = YtQ.Count > 0 ? YtQ[0] : null;
            if (n == null && _spotNext.Count > 0) { var s0 = _spotNext[0]; sp = (s0.title, s0.artist, s0.art); haveSp = true; }
        }
        if (n != null)
            return ",\"next\":true,\"nextTitle\":\"" + JEsc(n.title) + "\",\"nextArtist\":\"" + JEsc(n.channel) +
                   "\",\"nextArt\":\"https://i.ytimg.com/vi/" + n.id + "/mqdefault.jpg\"";
        if (haveSp)
            return ",\"next\":true,\"nextTitle\":\"" + JEsc(sp.title) + "\",\"nextArtist\":\"" + JEsc(sp.artist) + "\",\"nextArt\":\"" + JEsc(sp.art) + "\"";
        return ",\"next\":false,\"nextTitle\":\"\",\"nextArtist\":\"\",\"nextArt\":\"\"";
    }

    // Combined state for the OBS dock (one fetch: settings + YouTube queue + Spotify up-next + now playing).
    static string BuildQueueJson()
    {
        // Snapshot under _yt, then match Spotify up-next against our own request log under _rl - never hold
        // both locks at once (they're taken separately everywhere else, so this ordering can't deadlock).
        bool enabled, spEnabled, spConnected, reqOn, alive, showCard, showVideo, chatOn; int vol, maxSec;
        List<YtItem> yt; List<(string title, string artist, string art, string uri)> spNext; HashSet<string> skipSet;
        lock (_yt)
        {
            enabled = YtEnabled; spEnabled = SpotifyEnabled; spConnected = SpotifyConnected; reqOn = RequestsEnabled; chatOn = AnnounceChat; vol = YtVol; showCard = YtShowCard; showVideo = YtVideo; maxSec = MaxSongSec;
            alive = (DateTime.UtcNow - _lastPlayerSeen).TotalSeconds <= 8;
            yt = new List<YtItem>(YtQ);
            spNext = new List<(string, string, string, string)>(_spotNext);
            skipSet = new HashSet<string>(_skipUris, StringComparer.Ordinal);
        }
        // For each upcoming Spotify track: was it a viewer request (still-pending "queued" Req with that
        // uri), or Spotify's own autoplay/radio? reqUser[i] = requester name, or null for autoplay.
        var reqUser = new string[spNext.Count];
        lock (_rl)
        {
            var used = new HashSet<Req>();
            for (int i = 0; i < spNext.Count; i++)
            {
                var uri = spNext[i].uri;
                if (string.IsNullOrEmpty(uri)) continue;
                var req = Reqs.FindLast(x => x.uri == uri && x.status == "queued" && !used.Contains(x));
                if (req != null) { reqUser[i] = req.user ?? ""; used.Add(req); }   // one pending request labels at most one up-next slot
            }
        }

        var sb = new StringBuilder("{");
        sb.Append("\"enabled\":").Append(enabled ? "true" : "false");
        sb.Append(",\"spEnabled\":").Append(spEnabled ? "true" : "false");
        sb.Append(",\"spConnected\":").Append(spConnected ? "true" : "false");
        sb.Append(",\"reqEnabled\":").Append(reqOn ? "true" : "false");
        sb.Append(",\"chatAnnounce\":").Append(chatOn ? "true" : "false");
        sb.Append(",\"vol\":").Append(vol);
        sb.Append(",\"playerAlive\":").Append(alive ? "true" : "false");
        sb.Append(",\"showCard\":").Append(showCard ? "true" : "false");
        sb.Append(",\"showVideo\":").Append(showVideo ? "true" : "false");
        sb.Append(",\"maxSec\":").Append(maxSec);
        sb.Append(",\"outputEnabled\":").Append(OutputEnabled ? "true" : "false");
        sb.Append(",\"outputFormat\":\"").Append(JEsc(OutputFormat)).Append("\"");
        sb.Append(",\"splitOutput\":").Append(SplitOutput ? "true" : "false");
        sb.Append(",\"downloadCover\":").Append(DownloadCover ? "true" : "false");
        sb.Append(",\"pauseBehavior\":\"").Append(JEsc(PauseBehavior)).Append("\"");
        sb.Append(",\"customPauseText\":\"").Append(JEsc(CustomPauseText)).Append("\"");
        sb.Append(",\"maxQueue\":").Append(MaxQueueLength);
        sb.Append(",\"maxPerUser\":").Append(MaxRequestsPerUser);
        sb.Append(",\"cooldown\":").Append(SrCooldownSec);
        sb.Append(",\"userCooldown\":").Append(SrPerUserCooldownSec);
        sb.Append(",\"blockExplicit\":").Append(BlockExplicit ? "true" : "false");
        AppendJsonArray(sb, "blUsers", UserBlacklist);
        AppendJsonArray(sb, "blSongs", SongBlacklist);
        AppendJsonArray(sb, "blArtists", ArtistBlacklist);
        sb.Append(",\"botEnabled\":").Append(TwitchBotEnabled ? "true" : "false");
        sb.Append(",\"botConnected\":").Append(_botConnected ? "true" : "false");
        sb.Append(",\"botMode\":\"").Append(JEsc(TwitchAuthMode)).Append("\"");
        sb.Append(",\"botUser\":\"").Append(JEsc(TwitchBotUser ?? "")).Append("\"");
        sb.Append(",\"botChannel\":\"").Append(JEsc(TwitchChannel ?? "")).Append("\"");
        sb.Append(",\"botClientId\":\"").Append(JEsc(TwitchClientId ?? "")).Append("\"");
        sb.Append(",\"botHasToken\":").Append(!string.IsNullOrEmpty(TwitchBotToken) ? "true" : "false");
        sb.Append(",\"botHasSecret\":").Append(!string.IsNullOrEmpty(TwitchClientSecret) ? "true" : "false");
        sb.Append(",\"redemptionSource\":\"").Append(JEsc(RedemptionSource)).Append("\"");
        sb.Append(",\"manageReward\":").Append(ManageReward ? "true" : "false");
        sb.Append(",\"srForBits\":").Append(SrForBits ? "true" : "false");
        sb.Append(",\"yt\":[");
        for (int i = 0; i < yt.Count; i++)
        {
            var x = yt[i]; if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(JEsc(x.id)).Append("\",\"title\":\"").Append(JEsc(x.title))
              .Append("\",\"channel\":\"").Append(JEsc(x.channel)).Append("\",\"dur\":").Append(x.durSec)
              .Append(",\"user\":\"").Append(JEsc(x.user ?? "")).Append("\"}");
        }
        sb.Append("],\"spotifyNext\":[");
        for (int i = 0; i < spNext.Count; i++)
        {
            if (i > 0) sb.Append(',');
            bool requested = reqUser[i] != null;
            bool skip = !string.IsNullOrEmpty(spNext[i].uri) && skipSet.Contains(spNext[i].uri);
            sb.Append("{\"title\":\"").Append(JEsc(spNext[i].title)).Append("\",\"artist\":\"").Append(JEsc(spNext[i].artist))
              .Append("\",\"requested\":").Append(requested ? "true" : "false")
              .Append(",\"user\":\"").Append(JEsc(requested ? reqUser[i] : "")).Append("\",\"uri\":\"").Append(JEsc(spNext[i].uri))
              .Append("\",\"skip\":").Append(skip ? "true" : "false").Append("}");
        }
        sb.Append(']');
        sb.Append(",\"now\":").Append(GetNow()).Append('}');
        return sb.ToString();
    }

    // Dock commands (localhost only): skip / remove a queued YouTube item / volume / add a request.
    static async Task<string> HandleCmd(System.Collections.Specialized.NameValueCollection q)
    {
        switch ((q["cmd"] ?? "").ToLowerInvariant())
        {
            case "skip":
            {
                // Skip advances the QUEUE: a playing YouTube item is skipped; else if a YouTube item is
                // waiting, jump straight to it (pause Spotify, play it now); else skip the Spotify track.
                bool active, queued;
                lock (_yt) { active = YtActive != null; queued = YtActive == null && YtQ.Count > 0; if (queued) _skipToYt = true; }
                if (active) await FinishActive("skipped");
                else if (!queued) await SpotifySkipNext();   // queued -> the conductor starts it immediately (below)
                return "{\"ok\":true}";
            }
            case "remove":
            {
                string id = q["id"]; bool removed;
                lock (_yt) removed = YtQ.RemoveAll(x => x.id == id) > 0;
                if (removed) { MarkYtReq(id, "removed"); YouTube.DeleteCached(id); Log("Removed from queue: " + id); }
                return "{\"ok\":" + (removed ? "true" : "false") + "}";
            }
            case "skipsong":   // Spotify has no remove-from-queue API -> mark this uri to auto-skip when it plays
            {
                string uri = q["uri"];
                if (string.IsNullOrEmpty(uri)) return "{\"ok\":false}";
                bool marked;
                lock (_yt) { if (_skipUris.Contains(uri)) { _skipUris.Remove(uri); marked = false; } else { _skipUris.Add(uri); marked = true; } }
                Log((marked ? "Will auto-skip when it comes up: " : "Un-marked auto-skip: ") + uri);
                return "{\"ok\":true,\"skip\":" + (marked ? "true" : "false") + "}";
            }
            case "vol":
            {
                if (int.TryParse(q["v"], out var v)) lock (_yt) YtVol = Math.Clamp(v, 0, 100);
                return "{\"ok\":true}";
            }
            case "maxlen":
            {
                if (int.TryParse(q["min"], out var mnv)) { int sec = Math.Clamp(mnv, 1, 10) * 60; lock (_yt) MaxSongSec = sec; SetConfigValue("MaxSongSeconds", sec.ToString()); Log("Max song length set to " + (sec / 60) + " min from the dock."); }
                return "{\"ok\":true,\"maxSec\":" + MaxSongSec + "}";
            }
            case "card":
            {
                bool on = q["on"] == "1";
                lock (_yt) YtShowCard = on;
                try { File.WriteAllText(ShowCardFile, on ? "1" : "0"); } catch { }
                Log("Now-playing card " + (on ? "shown" : "hidden (audio-only overlay)"));
                return "{\"ok\":true,\"showCard\":" + (on ? "true" : "false") + "}";
            }
            case "video":
            {
                // Only affects items fetched AFTER this toggle - an already-downloaded/playing item keeps its own mode.
                bool on = q["on"] == "1";
                lock (_yt) YtVideo = on;
                try { File.WriteAllText(ShowVideoFile, on ? "1" : "0"); } catch { }
                Log("YouTube video " + (on ? "ON - new requests fetch + show the video" : "off - audio-only"));
                return "{\"ok\":true,\"showVideo\":" + (on ? "true" : "false") + "}";
            }
            case "output":
            {
                bool on = q["on"] == "1";
                OutputEnabled = on; SetConfigValue("OutputEnabled", on ? "1" : "0");
                if (on) { try { Directory.CreateDirectory(OutputDir); } catch { } }
                Log("OBS text files " + (on ? "enabled -> " + OutputDir : "disabled") + " from the dock.");
                return "{\"ok\":true,\"outputEnabled\":" + (on ? "true" : "false") + "}";
            }
            case "outcfg":   // OBS now-playing output settings - apply LIVE (PublishOutputs reads these each tick)
            {
                string k = (q["k"] ?? "").ToLowerInvariant(); string v = q["v"] ?? "";
                switch (k)
                {
                    case "format":    OutputFormat = string.IsNullOrEmpty(v) ? "{artist} - {title}" : v; SetConfigValue("OutputFormat", OutputFormat); break;
                    case "split":     SplitOutput = v == "1"; SetConfigValue("SplitOutput", v == "1" ? "1" : "0"); break;
                    case "cover":     DownloadCover = v == "1"; SetConfigValue("DownloadCover", v == "1" ? "1" : "0"); break;
                    case "pause":     { string p = v.ToLowerInvariant(); if (p != "clear" && p != "text") p = "nothing"; PauseBehavior = p; SetConfigValue("PauseBehavior", p); break; }
                    case "pausetext": CustomPauseText = v; SetConfigValue("CustomPauseText", v); break;
                    default: return "{\"ok\":false}";
                }
                lock (_outLock) { _outLastKey = null; _outLastArtUrl = null; }   // force a rewrite on the next publish so the change shows immediately
                Log("OBS output '" + k + "' updated from the dock.");
                return "{\"ok\":true}";
            }
            case "rule":
            {
                string k = (q["k"] ?? "").ToLowerInvariant(); string v = q["v"] ?? "";
                switch (k)
                {
                    case "maxqueue":     if (int.TryParse(v, out var a1)) { MaxQueueLength = Math.Max(0, a1); SetConfigValue("MaxQueueLength", MaxQueueLength.ToString()); } break;
                    case "maxperuser":   if (int.TryParse(v, out var a2)) { MaxRequestsPerUser = Math.Max(0, a2); SetConfigValue("MaxRequestsPerUser", MaxRequestsPerUser.ToString()); } break;
                    case "cooldown":     if (int.TryParse(v, out var a3)) { SrCooldownSec = Math.Max(0, a3); SetConfigValue("SrCooldownSec", SrCooldownSec.ToString()); } break;
                    case "usercooldown": if (int.TryParse(v, out var a4)) { SrPerUserCooldownSec = Math.Max(0, a4); SetConfigValue("SrPerUserCooldownSec", SrPerUserCooldownSec.ToString()); } break;
                    case "explicit":     BlockExplicit = v == "1"; SetConfigValue("BlockExplicit", BlockExplicit ? "1" : "0"); break;
                    default: return "{\"ok\":false}";
                }
                Log("Rule " + k + " set to '" + v + "' from the dock.");
                return "{\"ok\":true}";
            }
            case "blacklist":
            {
                bool ok = BlacklistEdit((q["kind"] ?? "").ToLowerInvariant(), (q["op"] ?? "").ToLowerInvariant(), q["val"] ?? "");
                return "{\"ok\":" + (ok ? "true" : "false") + "}";
            }
            case "twitchauth":   // app mode: run the one-time Twitch OAuth consent (opens the browser)
            {
                string status = await TwitchStartAuth();
                return "{\"ok\":true,\"status\":\"" + JEsc(status) + "\"}";
            }
            case "commands":   // return the chat-command definitions for the dock editor
                return "{\"commands\":" + JsonSerializer.Serialize(Commands) + "}";
            case "cmdedit":    // edit one field of one command (applies live)
            {
                bool ok = EditCommand(q["name"] ?? "", (q["field"] ?? "").ToLowerInvariant(), q["value"] ?? "");
                return "{\"ok\":" + (ok ? "true" : "false") + "}";
            }
            case "botcfg":   // NON-secret Twitch bot settings from the dock (tokens/secret stay in config.txt); needs a Restart to reconnect
            {
                string k = (q["k"] ?? "").ToLowerInvariant(); string v = q["v"] ?? "";
                switch (k)
                {
                    case "enabled":  TwitchBotEnabled = v == "1"; SetConfigValue("TwitchBotEnabled", v == "1" ? "1" : "0"); break;
                    case "mode":     { string mm = v.ToLowerInvariant() == "app" ? "app" : "irc"; TwitchAuthMode = mm; SetConfigValue("TwitchAuthMode", mm); break; }
                    case "botuser":  TwitchBotUser = v.Trim(); SetConfigValue("TwitchBotUsername", v.Trim()); break;
                    case "channel":  { string ch = v.Trim().TrimStart('#').ToLowerInvariant(); TwitchChannel = ch; SetConfigValue("TwitchChannel", ch); break; }
                    case "clientid": TwitchClientId = v.Trim(); SetConfigValue("TwitchClientId", v.Trim()); break;
                    case "redemptionsource": { string rs = v.ToLowerInvariant() == "twitch" ? "twitch" : "sb"; RedemptionSource = rs; SetConfigValue("RedemptionSource", rs); break; }
                    case "managereward": ManageReward = v == "1"; SetConfigValue("ManageReward", v == "1" ? "1" : "0"); break;
                    case "srforbits":    SrForBits = v == "1"; SetConfigValue("SrForBits", v == "1" ? "1" : "0"); break;
                    default: return "{\"ok\":false}";
                }
                Log("Bot setting '" + k + "' updated from the dock (Restart to apply).");
                return "{\"ok\":true}";
            }
            case "pause":
            {
                // Toggle play/pause of whatever is currently playing: the active YouTube item, else Spotify.
                bool ytActive; lock (_yt) ytActive = YtActive != null;
                if (ytActive)
                {
                    lock (_yt)
                    {
                        _ytPaused = !_ytPaused;                                    // the player applies it on its next poll
                        if (_ytPaused) _ytPauseStart = DateTime.UtcNow;
                        else _ytStarted += DateTime.UtcNow - _ytPauseStart;        // push start forward so paused time doesn't count toward the cap
                    }
                }
                else if (SpotifyConnected) { bool play; lock (_yt) { play = !_spotPlaying; _spotPlaying = play; _spotUserPaused = !play; } await SpotifyPlayback(play); }   // nothing YT playing -> toggle the Spotify bed (only if Spotify exists)
                return "{\"ok\":true}";
            }
            case "seek":
            {
                if (!int.TryParse(q["ms"], out var ms) || ms < 0) return "{\"ok\":false}";
                bool ytActive; lock (_yt) ytActive = YtActive != null;
                if (ytActive) { lock (_yt) { _ytSeekMs = ms; _ytSeekId++; } }   // the player seeks when seekId changes
                else await SpotifySeek(ms);
                return "{\"ok\":true}";
            }
            case "enable":
            {
                string src = (q["src"] ?? "").ToLowerInvariant();
                bool on = q["on"] == "1";
                if (src == "all") { lock (_yt) RequestsEnabled = on; SetConfigValue("RequestsEnabled", on ? "1" : "0"); Log("Song requests " + (on ? "enabled" : "disabled") + " from the dock."); await ApplyRewardState(); }
                else if (src == "sp") { lock (_yt) SpotifyEnabled = on; SetConfigValue("SpotifyEnabled", on ? "1" : "0"); Log("Spotify requests " + (on ? "enabled" : "disabled") + " from the dock."); await ApplyRewardState(); }
                else if (src == "yt") { lock (_yt) YtEnabled = on; SetConfigValue("YouTubeEnabled", on ? "1" : "0"); Log("YouTube requests " + (on ? "enabled" : "disabled") + " from the dock."); await ApplyRewardState(); }
                else if (src == "chat") { lock (_yt) AnnounceChat = on; SetConfigValue("AnnounceChat", on ? "1" : "0"); Log("Chat announcements " + (on ? "enabled" : "disabled") + " from the dock."); }
                else return "{\"ok\":false}";
                return "{\"ok\":true}";
            }
            case "stop":
            {
                Log("Stop requested from the dock.");
                _ = Task.Run(async () => { await Task.Delay(350); SignalStop(); });   // let the HTTP reply flush, then graceful-exit
                return "{\"ok\":true}";
            }
            case "restart":
            {
                Log("Restart requested from the dock.");
                _restarting = true;   // so GracefulStop doesn't pause the reward - the relaunched engine resyncs it
                try
                {
                    string root = Environment.GetEnvironmentVariable("SQ_ROOT");
                    string launcher = !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "SongRequests.exe"))
                        ? Path.Combine(root, "SongRequests.exe")
                        : System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    // wait ~2s for this instance to release the single-instance mutex, then relaunch headless
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & \"" + launcher + "\" --engine")
                    { UseShellExecute = false, CreateNoWindow = true };
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception e) { Log("Restart relaunch failed: " + e.Message); }
                _ = Task.Run(async () => { await Task.Delay(350); SignalStop(); });
                return "{\"ok\":true}";
            }
            case "add":
            {
                string text = (q["text"] ?? "").Trim();
                if (text.Length == 0) return "{\"ok\":false}";
                Log("Dock request: " + text);
                var r = await QueueSong(text, "streamer", fromStreamer: true);   // the streamer's own add box ignores the viewer requests-off gate
                AddRequest("streamer", text, r.track, r.uri, r.status);
                return "{\"ok\":true,\"status\":\"" + JEsc(r.status) + "\"" + (r.track != null ? ",\"track\":\"" + JEsc(r.track) + "\"" : "") + "}";
            }
        }
        return "{\"ok\":false}";
    }

    static string FirstString(JsonElement o, params string[] keys)
    {
        foreach (var k in keys) if (o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString().Length > 0) return v.GetString();
        return null;
    }
    static string Nested(JsonElement o, string parent, params string[] keys)
        => o.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object ? FirstString(p, keys) : null;

    static readonly object _ll = new object();
    static void Log(string m)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + m;
        try { lock (_ll) File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
    }
}
