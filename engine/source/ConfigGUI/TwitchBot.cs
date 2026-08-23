using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// ===== Native Twitch chat bot (Phase 3a: IRC transport) =====
// Adds a DIRECT Twitch chat connection for viewer commands (!sr, !song, !queue, ...), independent of
// Streamer.bot - which keeps handling channel-point redemptions/refunds exactly as before. The command
// engine here is transport-agnostic: DispatchCommand/RunCommand don't care how a line arrived, so the
// later OAuth/EventSub backend (3b) can feed the same pipeline. IRC is used now because it's the shortest
// path to a working command bot (a chat OAuth token + channel, no dev-app required).
//
// User levels from IRC tags: 0 everyone · 1 subscriber · 2 VIP · 3 moderator · 4 broadcaster.
// (Follower / sub-tier granularity needs Helix and arrives with the OAuth backend.)

// One chat command definition (persisted to commands.json, hand-editable). Triggers are stored WITHOUT the
// prefix, so changing CommandPrefix doesn't require rewriting them.
class Cmd
{
    public string name { get; set; }
    public List<string> triggers { get; set; }
    public bool enabled { get; set; } = true;
    public int level { get; set; }        // minimum user level (0..4)
    public int cd { get; set; }           // global cooldown (seconds, 0 = none)
    public int ucd { get; set; }          // per-user cooldown (seconds, 0 = none)
    public string resp { get; set; }      // response template (tokens: {user} {song} {next} {queue} {pos} {vol} {state} {commands} {votes} {args})
}

partial class Engine
{
    static bool TwitchBotEnabled;
    static string TwitchBotUser, TwitchBotToken, TwitchChannel;
    static string CmdPrefix = "!";
    static int VoteSkipCount = 3;
    static volatile bool _botConnected;
    static ClientWebSocket _ircWs;
    static readonly SemaphoreSlim _ircSend = new SemaphoreSlim(1, 1);
    static DateTime _lastIrcSend = DateTime.MinValue;

    static volatile List<Cmd> Commands = new List<Cmd>();
    static readonly object _cmdFileLock = new object();
    static string CommandsFile => System.IO.Path.Combine(DataDir, "commands.json");
    static readonly Dictionary<string, DateTime> _cmdLastGlobal = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, DateTime> _cmdLastUser = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    static readonly object _cdLock = new object();
    static readonly HashSet<string> _voteSkippers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ---- app (OAuth dev-app) mode: EventSub chat read + Helix send, auto-refreshing token ----
    static string TwitchAuthMode = "irc";   // "irc" (chat token) or "app" (Twitch dev app + OAuth)
    static string TwitchClientId, TwitchClientSecret;
    static int TwitchRedirectPort = 3737;
    static string TwitchAppRefresh, TwitchAppAccess;
    static DateTime TwitchAppExpires = DateTime.MinValue;
    static string TwitchUserId, TwitchUserLogin;   // the authorized account (posts + reads as this account)
    static readonly SemaphoreSlim _twTokLock = new SemaphoreSlim(1, 1);
    static volatile bool _twAuthRunning;
    static ClientWebSocket _esWs;   // the live EventSub socket (app mode)
    static string TwitchTokenFile => System.IO.Path.Combine(DataDir, "twitch_token.txt");

    // ---- Phase 3c: native channel-point rewards + refunds + bits (app mode only) ----
    static string RedemptionSource = "sb";   // "sb" (Streamer.bot, default) or "twitch" (native EventSub)
    static bool ManageReward; static int RewardCost = 100;   // create/reuse the SR reward via Helix
    static string ManagedRewardId;                           // the reward this app owns (only such rewards can be refunded natively)
    static bool SrForBits; static int MinimumBitsForSr = 100; static string SrForBitsKeyword = "";
    static string RewardIdFile => System.IO.Path.Combine(DataDir, "reward_id.txt");

    static void LoadTwitchConfig(Dictionary<string, string> c)
    {
        TwitchBotEnabled = Cfg(c, "TwitchBotEnabled", "0") == "1";
        TwitchBotUser = Cfg(c, "TwitchBotUsername", "");
        TwitchBotToken = Cfg(c, "TwitchBotToken", "");
        TwitchChannel = Cfg(c, "TwitchChannel", "").TrimStart('#').ToLowerInvariant();
        CmdPrefix = Cfg(c, "CommandPrefix", "!");
        VoteSkipCount = int.TryParse(Cfg(c, "VoteSkipCount", "3"), out var vs) ? Math.Max(2, vs) : 3;
        TwitchAuthMode = Cfg(c, "TwitchAuthMode", "irc").ToLowerInvariant();
        TwitchClientId = Cfg(c, "TwitchClientId", "");
        TwitchClientSecret = Cfg(c, "TwitchClientSecret", "");
        TwitchRedirectPort = int.TryParse(Cfg(c, "TwitchRedirectPort", "3737"), out var rp) ? rp : 3737;
        try { if (System.IO.File.Exists(TwitchTokenFile)) TwitchAppRefresh = System.IO.File.ReadAllText(TwitchTokenFile).Trim(); } catch { }
        RedemptionSource = Cfg(c, "RedemptionSource", "sb").ToLowerInvariant();
        ManageReward = Cfg(c, "ManageReward", "0") == "1";
        RewardCost = int.TryParse(Cfg(c, "RewardCost", "100"), out var rwc) ? Math.Max(1, rwc) : 100;
        SrForBits = Cfg(c, "SrForBits", "0") == "1";
        MinimumBitsForSr = int.TryParse(Cfg(c, "MinimumBitsForSr", "100"), out var mbs) ? Math.Max(1, mbs) : 100;
        SrForBitsKeyword = Cfg(c, "SrForBitsKeyword", "");
        try { if (System.IO.File.Exists(RewardIdFile)) ManagedRewardId = System.IO.File.ReadAllText(RewardIdFile).Trim(); } catch { }
        LoadCommands();
    }

    // ---- command definitions (file-backed, seeded with defaults) ----
    static List<Cmd> DefaultCommands() => new List<Cmd>
    {
        new Cmd { name = "sr",       triggers = new List<string>{ "sr", "songrequest", "request" }, level = 0, resp = "" },
        new Cmd { name = "song",     triggers = new List<string>{ "song", "nowplaying", "np" },     level = 0, resp = "@{user} now playing: {song}" },
        new Cmd { name = "next",     triggers = new List<string>{ "next" },                          level = 0, resp = "@{user} up next: {next}" },
        new Cmd { name = "queue",    triggers = new List<string>{ "queue", "q" },                    level = 0, cd = 5, resp = "@{user} queue: {queue}" },
        new Cmd { name = "pos",      triggers = new List<string>{ "pos", "position" },               level = 0, resp = "@{user} {pos}" },
        new Cmd { name = "remove",   triggers = new List<string>{ "remove", "wrongsong", "oops" },   level = 0, resp = "@{user} removed your last request." },
        new Cmd { name = "voteskip", triggers = new List<string>{ "voteskip" },                      level = 0, resp = "" },
        new Cmd { name = "skip",     triggers = new List<string>{ "skip" },                          level = 3, resp = "@{user} skipped: {song}" },
        new Cmd { name = "vol",      triggers = new List<string>{ "vol", "volume" },                 level = 3, resp = "@{user} YouTube volume {vol}%." },
        new Cmd { name = "play",     triggers = new List<string>{ "play", "resume" },                level = 3, resp = "playback resumed." },
        new Cmd { name = "pause",    triggers = new List<string>{ "pause" },                         level = 3, resp = "playback paused." },
        new Cmd { name = "togglesr", triggers = new List<string>{ "togglesr", "srtoggle" },         level = 3, resp = "song requests are now {state}." },
        new Cmd { name = "bansong",  triggers = new List<string>{ "bansong", "blocksong" },          level = 3, resp = "@{user} blocked the current song." },
        new Cmd { name = "cmds",     triggers = new List<string>{ "commands", "cmds", "help" },      level = 0, cd = 10, resp = "commands: {commands}" },
    };
    static void LoadCommands()
    {
        try
        {
            if (System.IO.File.Exists(CommandsFile))
            {
                var l = JsonSerializer.Deserialize<List<Cmd>>(System.IO.File.ReadAllText(CommandsFile));
                if (l != null && l.Count > 0) { Commands = MergeDefaults(l); return; }
            }
        }
        catch { }
        Commands = DefaultCommands();
        SaveCommands();
    }
    static List<Cmd> MergeDefaults(List<Cmd> loaded)   // add any command added in a newer version that the saved file predates
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in loaded) if (c.name != null) have.Add(c.name);
        foreach (var d in DefaultCommands()) if (!have.Contains(d.name)) loaded.Add(d);
        return loaded;
    }
    static void SaveCommands()
    { try { lock (_cmdFileLock) System.IO.File.WriteAllText(CommandsFile, JsonSerializer.Serialize(Commands, new JsonSerializerOptions { WriteIndented = true })); } catch { } }

    static Cmd FindCommand(string trig)
    {
        var cmds = Commands;
        foreach (var c in cmds) { if (c.triggers == null) continue; foreach (var t in c.triggers) if (string.Equals(t, trig, StringComparison.OrdinalIgnoreCase)) return c; }
        return null;
    }
    static string CommandList()
    {
        var parts = new List<string>(); var cmds = Commands;
        foreach (var c in cmds) if (c.enabled && c.triggers != null && c.triggers.Count > 0) parts.Add(CmdPrefix + c.triggers[0]);
        return string.Join(" ", parts);
    }
    // Edit one field of one command from the dock. Mutates the live Cmd (readers see it immediately) + persists. No restart needed.
    static bool EditCommand(string name, string field, string value)
    {
        Cmd c = null; var cmds = Commands;
        foreach (var x in cmds) if (string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase)) { c = x; break; }
        if (c == null) return false;
        switch (field)
        {
            case "enabled": c.enabled = value == "1"; break;
            case "level": if (int.TryParse(value, out var lv)) c.level = Math.Clamp(lv, 0, 4); break;
            case "cd": if (int.TryParse(value, out var cd)) c.cd = Math.Max(0, cd); break;
            case "ucd": if (int.TryParse(value, out var ucd)) c.ucd = Math.Max(0, ucd); break;
            case "resp": c.resp = value; break;
            case "triggers":
            {
                var list = new List<string>();
                foreach (var t in value.Replace(",", " ").Split(' ')) { var tt = t.Trim().TrimStart('!').ToLowerInvariant(); if (tt.Length > 0 && !list.Contains(tt)) list.Add(tt); }
                if (list.Count > 0) c.triggers = list;   // ignore an all-empty triggers edit (would make the command unreachable)
                break;
            }
            default: return false;
        }
        SaveCommands();
        Log("Command '" + name + "' " + field + " updated from the dock.");
        return true;
    }

    // ---- IRC connection ----
    static async Task TwitchChatLoop()   // dispatcher: run the transport the streamer configured
    {
        if (!TwitchBotEnabled) { Log("Twitch bot: off (set TwitchBotEnabled=1 to enable)."); return; }
        if (TwitchAuthMode == "app") { await TwitchAppLoop(); return; }
        await TwitchIrcLoop();
    }

    static async Task TwitchIrcLoop()
    {
        if (string.IsNullOrWhiteSpace(TwitchBotToken) || string.IsNullOrWhiteSpace(TwitchBotUser) || string.IsNullOrWhiteSpace(TwitchChannel))
        { Log("Twitch bot (IRC): missing username/token/channel - not connecting."); return; }
        string tok = TwitchBotToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? TwitchBotToken : "oauth:" + TwitchBotToken;
        while (true)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), CancellationToken.None);
                _ircWs = ws;
                await IrcRaw(ws, "CAP REQ :twitch.tv/tags twitch.tv/commands");
                await IrcRaw(ws, "PASS " + tok);
                await IrcRaw(ws, "NICK " + TwitchBotUser.ToLowerInvariant());
                await IrcRaw(ws, "JOIN #" + TwitchChannel);
                Log("Twitch bot: connecting to #" + TwitchChannel + " as " + TwitchBotUser + " ...");
                var buf = new byte[16384]; var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                    if (sb.Length > 1_000_000) sb.Clear();
                    string all = sb.ToString(); int nl;
                    while ((nl = all.IndexOf('\n')) >= 0)   // process each complete \r\n-terminated line; keep any partial tail
                    {
                        string line = all.Substring(0, nl).TrimEnd('\r');
                        all = all.Substring(nl + 1);
                        if (line.Length > 0) await HandleIrcLine(ws, line);
                    }
                    sb.Clear(); sb.Append(all);
                }
            }
            catch (Exception e) { Log("Twitch bot connection error: " + e.Message); }
            finally { _botConnected = false; _ircWs = null; }
            Log("Twitch bot: disconnected - retrying in 10s.");
            await Task.Delay(10000);
        }
    }

    static async Task IrcRaw(ClientWebSocket ws, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await _ircSend.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _ircSend.Release(); }
    }

    static async Task HandleIrcLine(ClientWebSocket ws, string line)
    {
        if (line.StartsWith("PING")) { await IrcRaw(ws, "PONG " + line.Substring(4)); return; }
        if (line.Contains(" 001 ")) { _botConnected = true; Log("Twitch bot: connected to #" + TwitchChannel + "."); return; }
        if (line.Contains("Login authentication failed") || line.Contains("Improperly formatted auth"))
        { Log("Twitch bot: LOGIN FAILED - check TwitchBotToken (a chat token with chat:read + chat:edit) and TwitchBotUsername."); return; }
        var pm = ParseIrc(line);
        if (pm == null || pm.Value.command != "PRIVMSG") return;
        await DispatchCommand(pm.Value.nick, pm.Value.level, pm.Value.text);
    }

    // ---- parsing (pure) ----
    struct IrcMsg { public string nick, command, channel, text; public int level; }
    static IrcMsg? ParseIrc(string line)
    {
        try
        {
            Dictionary<string, string> tags = null;
            string rest = line;
            if (rest.StartsWith("@")) { int sp = rest.IndexOf(' '); if (sp < 0) return null; tags = ParseTags(rest.Substring(1, sp - 1)); rest = rest.Substring(sp + 1); }
            if (!rest.StartsWith(":")) return null;
            int sp2 = rest.IndexOf(' '); if (sp2 < 0) return null;
            string prefix = rest.Substring(1, sp2 - 1);
            string nick = prefix.Contains('!') ? prefix.Substring(0, prefix.IndexOf('!')) : prefix;
            rest = rest.Substring(sp2 + 1);
            int sp3 = rest.IndexOf(' ');
            string command = sp3 < 0 ? rest : rest.Substring(0, sp3);
            string paramsPart = sp3 < 0 ? "" : rest.Substring(sp3 + 1);
            string channel = "", text = "";
            if (command == "PRIVMSG")
            {
                int colon = paramsPart.IndexOf(" :");
                if (colon >= 0) { channel = paramsPart.Substring(0, colon).Trim(); text = paramsPart.Substring(colon + 2); }
                else channel = paramsPart.Trim();
            }
            if (tags != null && tags.TryGetValue("display-name", out var dn) && !string.IsNullOrEmpty(dn)) nick = dn;
            return new IrcMsg { nick = nick, command = command, channel = channel, text = text, level = LevelFromTags(tags) };
        }
        catch { return null; }
    }
    static Dictionary<string, string> ParseTags(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(';')) { int eq = part.IndexOf('='); if (eq < 0) d[part] = ""; else d[part.Substring(0, eq)] = part.Substring(eq + 1); }
        return d;
    }
    static int LevelFromTags(Dictionary<string, string> tags)
    {
        if (tags == null) return 0;
        string badges = tags.TryGetValue("badges", out var b) ? (b ?? "") : "";
        if (badges.Contains("broadcaster/")) return 4;
        if ((tags.TryGetValue("mod", out var m) && m == "1") || badges.Contains("moderator/")) return 3;
        if (badges.Contains("vip/") || (tags.TryGetValue("vip", out var v) && v == "1")) return 2;
        if ((tags.TryGetValue("subscriber", out var s) && s == "1") || badges.Contains("subscriber/")) return 1;
        return 0;
    }

    // ---- dispatch + cooldowns ----
    static async Task DispatchCommand(string user, int level, string text)
    {
        text = (text ?? "").Trim();
        if (text.Length <= CmdPrefix.Length || !text.StartsWith(CmdPrefix, StringComparison.Ordinal)) return;
        string body = text.Substring(CmdPrefix.Length);
        int sp = body.IndexOf(' ');
        string trig = (sp < 0 ? body : body.Substring(0, sp)).ToLowerInvariant();
        string args = sp < 0 ? "" : body.Substring(sp + 1).Trim();
        var cmd = FindCommand(trig);
        if (cmd == null || !cmd.enabled) return;
        if (level < cmd.level) return;                         // not allowed - stay silent (no permission spam)
        if (level < 3 && OnCommandCooldown(cmd, user)) return; // mods/broadcaster bypass cooldowns
        string reply = await RunCommand(cmd, user, level, args);
        if (level < 3) MarkCommandUsed(cmd, user);
        if (!string.IsNullOrWhiteSpace(reply)) await SendChat(reply);
    }
    static bool OnCommandCooldown(Cmd cmd, string user)
    {
        var now = DateTime.UtcNow;
        lock (_cdLock)
        {
            if (cmd.cd > 0 && _cmdLastGlobal.TryGetValue(cmd.name, out var g) && (now - g).TotalSeconds < cmd.cd) return true;
            if (cmd.ucd > 0) { string k = cmd.name + "" + user; if (_cmdLastUser.TryGetValue(k, out var u) && (now - u).TotalSeconds < cmd.ucd) return true; }
        }
        return false;
    }
    static void MarkCommandUsed(Cmd cmd, string user)
    { var now = DateTime.UtcNow; lock (_cdLock) { _cmdLastGlobal[cmd.name] = now; _cmdLastUser[cmd.name + "" + user] = now; } }

    // Fill a response template: {user} first, then any command-specific tokens; unknown tokens are left as-is.
    static string Reply(string tmpl, string user, params (string k, string v)[] toks)
    {
        if (string.IsNullOrEmpty(tmpl)) return "";
        string s = tmpl.Replace("{user}", user ?? "");
        foreach (var (k, v) in toks) s = s.Replace("{" + k + "}", v ?? "");
        return s;
    }

    static async Task<string> RunCommand(Cmd cmd, string user, int level, string args)
    {
        switch (cmd.name)
        {
            case "sr":
                if (string.IsNullOrWhiteSpace(args)) return "@" + user + " add a song name or link after the command.";
                var r = await QueueSong(args, user);
                return AnnounceMessage(user, r.status, r.track) ?? ("@" + user + " " + r.status);
            case "song":     return Reply(cmd.resp, user, ("song", NowText()));
            case "next":     return Reply(cmd.resp, user, ("next", NextText()));
            case "queue":    return Reply(cmd.resp, user, ("queue", QueueText()));
            case "pos":      return Reply(cmd.resp, user, ("pos", PosText(user)));
            case "remove":   BotRemoveLast(user); return Reply(cmd.resp, user);
            case "skip":     { string s = NowText(); await BotSkip(); return Reply(cmd.resp, user, ("song", s)); }
            case "vol":
                if (int.TryParse(args.Trim(), out var nv)) { lock (_yt) YtVol = Math.Clamp(nv, 0, 100); SetConfigValue("YouTubeVolume", YtVol.ToString()); }
                return Reply(cmd.resp, user, ("vol", YtVol.ToString()));
            case "play":     await BotPlay(true);  return Reply(cmd.resp, user);
            case "pause":    await BotPlay(false); return Reply(cmd.resp, user);
            case "togglesr": { bool on; lock (_yt) { RequestsEnabled = !RequestsEnabled; on = RequestsEnabled; } SetConfigValue("RequestsEnabled", on ? "1" : "0"); await ApplyRewardState(); return Reply(cmd.resp, user, ("state", on ? "on" : "off")); }
            case "bansong":  await BanCurrentSong(); return Reply(cmd.resp, user);
            case "voteskip":
            {
                int need = VoteSkipCount, have; lock (_cdLock) { _voteSkippers.Add(user); have = _voteSkippers.Count; }
                if (have >= need) { await BotSkip(); return "@" + user + " vote passed - skipping. (" + have + "/" + need + ")"; }
                return "@" + user + " voted to skip (" + have + "/" + need + ")";
            }
            case "cmds":     return Reply(cmd.resp, user, ("commands", CommandList()));
            default:         return null;
        }
    }

    static async Task SendChat(string msg)   // dispatcher: sanitize once, then route to the active transport
    {
        msg = (msg ?? "").Replace("\r", " ").Replace("\n", " ");
        if (msg.Length > 450) msg = msg.Substring(0, 449) + "…";
        if (string.IsNullOrWhiteSpace(msg)) return;
        if (TwitchAuthMode == "app") { await SendChatApp(msg); return; }
        await SendChatIrc(msg);
    }

    static async Task SendChatIrc(string msg)
    {
        var ws = _ircWs; if (ws == null || ws.State != WebSocketState.Open) return;
        await _ircSend.WaitAsync();
        try
        {
            var since = (DateTime.UtcNow - _lastIrcSend).TotalMilliseconds;   // simple ~1.5s spacing to stay well under Twitch's rate limit
            if (since < 1500) await Task.Delay(1500 - (int)since);
            _lastIrcSend = DateTime.UtcNow;
            var bytes = Encoding.UTF8.GetBytes("PRIVMSG #" + TwitchChannel + " :" + msg + "\r\n");
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) { Log("Twitch bot send failed: " + e.Message); }
        finally { _ircSend.Release(); }
    }

    // ---- command actions (reuse the same state/flow as the dock) ----
    static string NowText()
    {
        try
        {
            var j = JsonDocument.Parse(GetNow()).RootElement;
            if (!(j.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True)) return "nothing playing";
            string t = j.TryGetProperty("title", out var ti) ? ti.GetString() : "";
            string a = j.TryGetProperty("artist", out var ar) ? ar.GetString() : "";
            return string.IsNullOrEmpty(a) ? (string.IsNullOrEmpty(t) ? "nothing playing" : t) : a + " - " + t;
        }
        catch { return "nothing playing"; }
    }
    static string NextText()
    {
        try
        {
            var j = JsonDocument.Parse(GetNow()).RootElement;
            if (j.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.True)
            {
                string t = j.TryGetProperty("nextTitle", out var ti) ? ti.GetString() : "";
                string a = j.TryGetProperty("nextArtist", out var ar) ? ar.GetString() : "";
                return string.IsNullOrEmpty(a) ? t : a + " - " + t;
            }
        }
        catch { }
        return "nothing queued";
    }
    static string QueueText()
    {
        var parts = new List<string>();
        lock (_yt)
        {
            foreach (var it in YtQ) { if (parts.Count >= 3) break; parts.Add(it.title); }
            foreach (var s in _spotNext) { if (parts.Count >= 3) break; parts.Add(s.title); }
        }
        return parts.Count == 0 ? "the queue is empty" : string.Join(" | ", parts);
    }
    static string PosText(string user)
    {
        int mine = CountOutstandingForUser(user), total = CountOutstanding();
        return mine == 0 ? "you have no songs in the queue." : "you have " + mine + " song(s) in the queue (queue size " + total + ").";
    }
    static async Task BotSkip()
    {
        bool active, queued;
        lock (_yt) { active = YtActive != null; queued = YtActive == null && YtQ.Count > 0; if (queued) _skipToYt = true; }
        if (active) await FinishActive("skipped");
        else if (!queued) await SpotifySkipNext();
        lock (_cdLock) _voteSkippers.Clear();
    }
    static async Task BotPlay(bool play)
    {
        bool ytActive; lock (_yt) ytActive = YtActive != null;
        if (ytActive)
        {
            lock (_yt) { _ytPaused = !play; if (_ytPaused) _ytPauseStart = DateTime.UtcNow; else _ytStarted += DateTime.UtcNow - _ytPauseStart; }
        }
        else if (SpotifyConnected) { lock (_yt) { _spotPlaying = play; _spotUserPaused = !play; } await SpotifyPlayback(play); }
    }
    static void BotRemoveLast(string user)
    {
        Req r; lock (_rl) r = Reqs.FindLast(x => x.status == "queued" && string.Equals(x.user, user, StringComparison.OrdinalIgnoreCase));
        if (r == null || r.uri == null) return;
        if (r.uri.StartsWith("youtube:video:", StringComparison.Ordinal))
        {
            string id = r.uri.Substring("youtube:video:".Length);
            lock (_yt) YtQ.RemoveAll(x => x.id == id);
            MarkYtReq(id, "removed"); YouTube.DeleteCached(id);
        }
        else if (r.uri.StartsWith("spotify:", StringComparison.Ordinal))
        {
            lock (_yt) _skipUris.Add(r.uri);
            lock (_rl) { r.status = "removed"; SaveRequests(); }
        }
    }
    static async Task BanCurrentSong()
    {
        string t = ""; try { var j = JsonDocument.Parse(GetNow()).RootElement; if (j.TryGetProperty("title", out var ti)) t = ti.GetString(); } catch { }
        if (!string.IsNullOrEmpty(t)) BlacklistEdit("song", "add", t);
        await BotSkip();
    }

    // =====================================================================================
    // App mode (Twitch dev app + OAuth): auto-refreshing token, EventSub chat read, Helix send.
    // Posts + reads as the single authorized account (broadcaster == sender). Same command engine.
    // =====================================================================================
    static async Task TwitchAppLoop()
    {
        if (string.IsNullOrWhiteSpace(TwitchClientId) || string.IsNullOrWhiteSpace(TwitchClientSecret))
        { Log("Twitch bot (app): set TwitchClientId + TwitchClientSecret (from your Twitch dev app), then restart."); return; }
        if (string.IsNullOrWhiteSpace(TwitchAppRefresh))   // not authorized yet -> run the one-time browser consent
        {
            Log("Twitch bot (app): not authorized yet - opening the browser to connect...");
            await TwitchAuthFlow();
            if (string.IsNullOrWhiteSpace(TwitchAppRefresh)) { Log("Twitch bot (app): not authorized - use Connect Twitch (dock) or restart to retry."); return; }
        }
        while (true)
        {
            try
            {
                await EnsureTwitchToken();
                if (!await ResolveTwitchUser()) { Log("Twitch bot (app): couldn't resolve the account - reauthorize (Connect Twitch)."); await Task.Delay(30000); continue; }
                if (ManageReward && string.IsNullOrEmpty(ManagedRewardId)) await EnsureReward();
                await RunEventSub();   // returns when the socket drops / reconnect is requested
            }
            catch (Exception e) { Log("Twitch bot (app) error: " + e.Message); }
            _botConnected = false;
            Log("Twitch bot (app): reconnecting in 10s.");
            await Task.Delay(10000);
        }
    }

    static async Task EnsureTwitchToken()
    {
        if (DateTime.UtcNow < TwitchAppExpires && !string.IsNullOrEmpty(TwitchAppAccess)) return;
        await _twTokLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow < TwitchAppExpires && !string.IsNullOrEmpty(TwitchAppAccess)) return;
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { {"grant_type","refresh_token"}, {"refresh_token",TwitchAppRefresh}, {"client_id",TwitchClientId}, {"client_secret",TwitchClientSecret} });
            using var resp = await Http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new Exception("token refresh " + (int)resp.StatusCode + ": " + body);
            using var doc = JsonDocument.Parse(body); var j = doc.RootElement;
            TwitchAppAccess = j.GetProperty("access_token").GetString();
            TwitchAppExpires = DateTime.UtcNow.AddSeconds((j.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600) - 60);
            if (j.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
            { TwitchAppRefresh = rt.GetString(); try { System.IO.File.WriteAllText(TwitchTokenFile, TwitchAppRefresh); } catch { } }
        }
        finally { _twTokLock.Release(); }
    }

    static async Task<bool> ResolveTwitchUser()
    {
        if (!string.IsNullOrEmpty(TwitchUserId)) return true;
        var j = await HelixGet("https://api.twitch.tv/helix/users");
        if (j == null) return false;
        var data = j.Value.GetProperty("data");
        if (data.GetArrayLength() == 0) return false;
        TwitchUserId = data[0].GetProperty("id").GetString();
        TwitchUserLogin = data[0].GetProperty("login").GetString();
        return true;
    }

    static async Task<JsonElement?> HelixGet(string url)
    {
        await EnsureTwitchToken();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TwitchAppAccess);
        req.Headers.Add("Client-Id", TwitchClientId);
        using var resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) { Log("Helix GET " + (int)resp.StatusCode + ": " + body); return null; }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    static async Task RunEventSub()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"), CancellationToken.None);
        _esWs = ws;
        var buf = new byte[16384]; var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            if (res.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
            if (!res.EndOfMessage) continue;
            string msg = sb.ToString(); sb.Clear();
            JsonDocument doc; try { doc = JsonDocument.Parse(msg); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                string mtype = root.TryGetProperty("metadata", out var md) && md.TryGetProperty("message_type", out var mt) ? mt.GetString() : "";
                if (mtype == "session_welcome")
                {
                    string sid = root.GetProperty("payload").GetProperty("session").GetProperty("id").GetString();
                    if (await SubscribeAll(sid)) { _botConnected = true; Log("Twitch bot (app): connected as " + (TwitchUserLogin ?? "?") + " (chat" + (RedemptionSource == "twitch" ? " + redemptions" : "") + (SrForBits ? " + bits" : "") + ")."); }
                    else { Log("Twitch bot (app): chat subscription failed - check the token scopes (user:read:chat) and reauthorize."); break; }
                }
                else if (mtype == "session_reconnect") { Log("Twitch bot (app): session_reconnect - reconnecting."); break; }
                else if (mtype == "revocation") { Log("Twitch bot (app): subscription revoked by Twitch (token/scope changed) - reconnecting."); break; }
                else if (mtype == "notification")
                {
                    string subType = md.TryGetProperty("subscription_type", out var stp) ? stp.GetString() : "";
                    if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("event", out var ev))
                    {
                        if (subType == "channel.chat.message") HandleChatNotification(ev);
                        else if (subType == "channel.channel_points_custom_reward_redemption.add") { var evc = ev.Clone(); _ = HandleRedemption(evc); }   // clone: the JsonDocument is disposed when this scope exits
                        else if (subType == "channel.cheer") { var evc = ev.Clone(); _ = HandleCheer(evc); }
                    }
                }
                // session_keepalive: nothing to do (presence of traffic is the keepalive)
            }
        }
        _esWs = null;
    }

    static async Task<bool> SubscribeAll(string sessionId)
    {
        bool chat = await SubscribeEventSub(sessionId, "channel.chat.message", "1", "\"broadcaster_user_id\":\"" + TwitchUserId + "\",\"user_id\":\"" + TwitchUserId + "\"");
        if (RedemptionSource == "twitch")
            await SubscribeEventSub(sessionId, "channel.channel_points_custom_reward_redemption.add", "1", "\"broadcaster_user_id\":\"" + TwitchUserId + "\"");
        if (SrForBits)
            await SubscribeEventSub(sessionId, "channel.cheer", "1", "\"broadcaster_user_id\":\"" + TwitchUserId + "\"");
        return chat;   // chat is the essential subscription; the others are best-effort (log on failure)
    }
    static async Task<bool> SubscribeEventSub(string sessionId, string type, string version, string conditionJson)
    {
        await EnsureTwitchToken();
        string body = "{\"type\":\"" + type + "\",\"version\":\"" + version + "\",\"condition\":{" + conditionJson
            + "},\"transport\":{\"method\":\"websocket\",\"session_id\":\"" + sessionId + "\"}}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/eventsub/subscriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TwitchAppAccess);
        req.Headers.Add("Client-Id", TwitchClientId);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return true;
        Log("EventSub subscribe (" + type + ") " + (int)resp.StatusCode + ": " + await resp.Content.ReadAsStringAsync());
        return false;
    }

    static void HandleChatNotification(JsonElement ev)
    {
        try
        {
            string user = ev.TryGetProperty("chatter_user_name", out var un) && !string.IsNullOrEmpty(un.GetString()) ? un.GetString()
                        : (ev.TryGetProperty("chatter_user_login", out var ul) ? ul.GetString() : "");
            string text = ev.TryGetProperty("message", out var m) && m.TryGetProperty("text", out var t) ? t.GetString() : "";
            _ = DispatchCommand(user, LevelFromBadges(ev), text);
        }
        catch { }
    }

    static int LevelFromBadges(JsonElement ev)
    {
        int lvl = 0;
        if (ev.TryGetProperty("badges", out var badges) && badges.ValueKind == JsonValueKind.Array)
            foreach (var b in badges.EnumerateArray())
            {
                string set = b.TryGetProperty("set_id", out var s) ? s.GetString() : "";
                if (set == "broadcaster") lvl = Math.Max(lvl, 4);
                else if (set == "moderator") lvl = Math.Max(lvl, 3);
                else if (set == "vip") lvl = Math.Max(lvl, 2);
                else if (set == "subscriber" || set == "founder") lvl = Math.Max(lvl, 1);
            }
        return lvl;
    }

    static async Task SendChatApp(string msg)
    {
        try
        {
            await EnsureTwitchToken();
            string body = "{\"broadcaster_id\":\"" + TwitchUserId + "\",\"sender_id\":\"" + TwitchUserId + "\",\"message\":\"" + JEsc(msg) + "\"}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/chat/messages");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TwitchAppAccess);
            req.Headers.Add("Client-Id", TwitchClientId);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) Log("Twitch send " + (int)resp.StatusCode + ": " + await resp.Content.ReadAsStringAsync());
        }
        catch (Exception e) { Log("Twitch send failed: " + e.Message); }
    }

    // Dock "Connect Twitch" trigger -> run the one-time browser consent (non-blocking).
    static Task<string> TwitchStartAuth()
    {
        if (string.IsNullOrWhiteSpace(TwitchClientId) || string.IsNullOrWhiteSpace(TwitchClientSecret)) return Task.FromResult("set TwitchClientId/TwitchClientSecret first");
        if (_twAuthRunning) return Task.FromResult("already connecting");
        _ = Task.Run(TwitchAuthFlow);
        return Task.FromResult("started - approve in the browser");
    }

    static async Task TwitchAuthFlow()
    {
        if (_twAuthRunning) return;
        _twAuthRunning = true;
        try
        {
            string redirect = "http://localhost:" + TwitchRedirectPort + "/twitchcallback";
            string scopes = "user:read:chat user:write:chat user:bot channel:bot channel:manage:redemptions channel:read:redemptions bits:read";
            string state = Guid.NewGuid().ToString("N");
            string url = "https://id.twitch.tv/oauth2/authorize?response_type=code&client_id=" + Uri.EscapeDataString(TwitchClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&scope=" + Uri.EscapeDataString(scopes) + "&state=" + state;
            using var l = new HttpListener();
            l.Prefixes.Add("http://localhost:" + TwitchRedirectPort + "/");
            try { l.Start(); }
            catch (Exception e) { Log("Twitch auth: can't listen on port " + TwitchRedirectPort + " (" + e.Message + ") - set TwitchRedirectPort to a free port that matches your app's redirect URI."); return; }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { Log("Twitch auth: open this URL to authorize -> " + url); }
            Log("Twitch auth: waiting for approval in the browser (redirect URI must be " + redirect + " in your Twitch app)...");
            var ctxTask = l.GetContextAsync();
            if (await Task.WhenAny(ctxTask, Task.Delay(180000)) != ctxTask) { Log("Twitch auth: timed out (3 min)."); return; }
            var ctx = ctxTask.Result;
            string code = ctx.Request.QueryString["code"]; string st = ctx.Request.QueryString["state"];
            string html = "<html><body style='font-family:sans-serif;background:#111;color:#ddd;text-align:center;padding:40px'><h2>Twitch connected — you can close this tab.</h2></body></html>";
            var hb = Encoding.UTF8.GetBytes(html);
            try { ctx.Response.ContentType = "text/html"; ctx.Response.ContentLength64 = hb.Length; ctx.Response.OutputStream.Write(hb, 0, hb.Length); ctx.Response.Close(); } catch { }
            if (st != state || string.IsNullOrEmpty(code)) { Log("Twitch auth: invalid callback (state mismatch or denied)."); return; }
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { {"grant_type","authorization_code"}, {"code",code}, {"client_id",TwitchClientId}, {"client_secret",TwitchClientSecret}, {"redirect_uri",redirect} });
            using var resp = await Http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { Log("Twitch auth exchange " + (int)resp.StatusCode + ": " + body); return; }
            using var doc = JsonDocument.Parse(body); var j = doc.RootElement;
            TwitchAppAccess = j.GetProperty("access_token").GetString();
            TwitchAppExpires = DateTime.UtcNow.AddSeconds((j.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600) - 60);
            TwitchAppRefresh = j.GetProperty("refresh_token").GetString();
            try { System.IO.File.WriteAllText(TwitchTokenFile, TwitchAppRefresh); } catch { }
            TwitchUserId = null; await ResolveTwitchUser();
            Log("Twitch auth: connected as " + (TwitchUserLogin ?? "?") + ". The bot will start on the next connect cycle (or restart the engine).");
        }
        catch (Exception e) { Log("Twitch auth error: " + e.Message); }
        finally { _twAuthRunning = false; }
    }

    static async Task<JsonElement?> HelixPost(string url, string body)
    {
        await EnsureTwitchToken();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TwitchAppAccess);
        req.Headers.Add("Client-Id", TwitchClientId);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        string rb = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) { Log("Helix POST " + (int)resp.StatusCode + ": " + rb); return null; }
        if (string.IsNullOrWhiteSpace(rb)) return null;
        using var doc = JsonDocument.Parse(rb);
        return doc.RootElement.Clone();
    }

    // Ensure the channel-point reward exists (reuse by title, else create). Only a reward THIS app creates
    // can be refunded/fulfilled natively. Requires channel:manage:redemptions + an Affiliate/Partner channel.
    static async Task EnsureReward()
    {
        try
        {
            var j = await HelixGet("https://api.twitch.tv/helix/channel_points/custom_rewards?only_manageable_rewards=true&broadcaster_id=" + TwitchUserId);
            if (j != null && j.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var rw in data.EnumerateArray())
                    if (string.Equals(rw.GetProperty("title").GetString(), RewardName, StringComparison.OrdinalIgnoreCase))
                    { ManagedRewardId = rw.GetProperty("id").GetString(); try { System.IO.File.WriteAllText(RewardIdFile, ManagedRewardId); } catch { } Log("Twitch: managing existing reward '" + RewardName + "'."); return; }
            string body = "{\"title\":\"" + JEsc(RewardName) + "\",\"cost\":" + RewardCost + ",\"is_user_input_required\":true,\"prompt\":\"Enter a song name or link\"}";
            var created = await HelixPost("https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id=" + TwitchUserId, body);
            if (created != null && created.Value.TryGetProperty("data", out var cd) && cd.GetArrayLength() > 0)
            { ManagedRewardId = cd[0].GetProperty("id").GetString(); try { System.IO.File.WriteAllText(RewardIdFile, ManagedRewardId); } catch { } Log("Twitch: created reward '" + RewardName + "' (" + RewardCost + " pts)."); }
            else Log("Twitch: couldn't create the reward - needs channel:manage:redemptions and an Affiliate/Partner channel.");
        }
        catch (Exception e) { Log("Twitch reward setup error: " + e.Message); }
    }

    // Native channel-point redemption (app mode, RedemptionSource=twitch). Mirrors the Streamer.bot path:
    // queue -> announce -> refund on failure / fulfill on success.
    static async Task HandleRedemption(JsonElement ev)
    {
        try
        {
            string redId = ev.TryGetProperty("id", out var i) ? i.GetString() : "";
            string user = ev.TryGetProperty("user_name", out var u) && !string.IsNullOrEmpty(u.GetString()) ? u.GetString()
                        : (ev.TryGetProperty("user_login", out var ul) ? ul.GetString() : "someone");
            string input = ev.TryGetProperty("user_input", out var ui) ? ui.GetString() : "";
            string rewardId = "", rewardTitle = "";
            if (ev.TryGetProperty("reward", out var rw) && rw.ValueKind == JsonValueKind.Object)
            { rewardId = rw.TryGetProperty("id", out var ri) ? ri.GetString() : ""; rewardTitle = rw.TryGetProperty("title", out var rt) ? rt.GetString() : ""; }
            if (!string.IsNullOrEmpty(ManagedRewardId)) { if (rewardId != ManagedRewardId) return; }                       // filter to our reward
            else if (!string.IsNullOrEmpty(RewardName) && !string.Equals(rewardTitle, RewardName, StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(input)) { Log(user + " redeemed but sent no text."); return; }
            Log(user + " redeemed (twitch): " + input);
            var r = await QueueSong(input.Trim(), user);
            AddRequest(user, input.Trim(), r.track, r.uri, r.status);
            string chat = AnnounceMessage(user, r.status, r.track);
            if (r.status == "queued") await UpdateRedemption(rewardId, redId, "FULFILLED");
            else if (RefundFailed && !string.IsNullOrEmpty(redId)) { await UpdateRedemption(rewardId, redId, "CANCELED"); if (chat != null) chat += " (points refunded)"; }
            if (AnnounceChat && chat != null) await SendChat(chat);
        }
        catch (Exception e) { Log("Twitch redemption error: " + e.Message); }
    }

    static async Task UpdateRedemption(string rewardId, string redId, string status)
    {
        if (string.IsNullOrEmpty(rewardId) || string.IsNullOrEmpty(redId)) return;
        try
        {
            await EnsureTwitchToken();
            string url = "https://api.twitch.tv/helix/channel_points/custom_rewards/redemptions?broadcaster_id=" + TwitchUserId
                + "&reward_id=" + Uri.EscapeDataString(rewardId) + "&id=" + Uri.EscapeDataString(redId);
            using var req = new HttpRequestMessage(HttpMethod.Patch, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TwitchAppAccess);
            req.Headers.Add("Client-Id", TwitchClientId);
            req.Content = new StringContent("{\"status\":\"" + status + "\"}", Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) Log("Redemption " + status + " " + (int)resp.StatusCode + ": " + await resp.Content.ReadAsStringAsync() + " (only a reward this app created can be updated)");
        }
        catch (Exception e) { Log("Redemption update failed: " + e.Message); }
    }

    // Bits/cheer request (app mode, SrForBits). Note: bits aren't refundable, so a rejected cheer request
    // simply doesn't queue. Song text = the cheer message (with the keyword, if set, stripped).
    static async Task HandleCheer(JsonElement ev)
    {
        try
        {
            if (!SrForBits) return;
            int bits = ev.TryGetProperty("bits", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
            if (bits < MinimumBitsForSr) return;
            string user = ev.TryGetProperty("user_name", out var u) && !string.IsNullOrEmpty(u.GetString()) ? u.GetString() : "someone";
            string song = ev.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
            if (!string.IsNullOrEmpty(SrForBitsKeyword))
            { int idx = song.IndexOf(SrForBitsKeyword, StringComparison.OrdinalIgnoreCase); if (idx >= 0) song = song.Substring(idx + SrForBitsKeyword.Length); }
            song = song.Trim();
            if (song.Length == 0) return;
            Log(user + " cheered " + bits + " bits for a song: " + song);
            var r = await QueueSong(song, user);
            AddRequest(user, song, r.track, r.uri, r.status);
            if (AnnounceChat) { string chat = AnnounceMessage(user, r.status, r.track); if (chat != null) await SendChat(chat); }
        }
        catch (Exception e) { Log("Twitch cheer error: " + e.Message); }
    }
}
