using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// ===== YouTube resolver + audio fetcher =====
// Turns a YouTube / YouTube Music request (a link, an 11-char id, or a text search) into a video id +
// duration + flags (for the length cap and guardrails), AND downloads the audio-only track so the OBS
// overlay can play it as a plain <audio> file. We deliberately do NOT embed YouTube's IFrame player -
// OBS's browser (CEF) is refused by YouTube's embed host-check (error 153), so local audio is the only
// path that reliably plays. yt-dlp.exe is fetched on demand into the data dir and refreshed periodically,
// so we don't ship a stale copy (yt-dlp breaks often). No transcoding (no ffmpeg needed): we grab a
// single browser-playable audio format (m4a/AAC or webm/opus, both decode in OBS's CEF).
static class YouTube
{
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SongRequests");
    static string ExePath => Path.Combine(DataDir, "yt-dlp.exe");
    static string FfmpegPath => Path.Combine(DataDir, "ffmpeg.exe");   // for merging separate video+audio streams (HD video mode)
    public static string AudioDir => Path.Combine(DataDir, "ytaudio");   // cached media files, <id>.<ext> (audio-only or progressive/merged video)
    const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    // yt-dlp's own ffmpeg build - one zip, extract ffmpeg.exe/ffprobe.exe next to yt-dlp. Only fetched the first time video mode needs it.
    const string FfmpegZipUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
    static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(3);   // re-pull latest yt-dlp every few days
    static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    static readonly SemaphoreSlim _acquire = new SemaphoreSlim(1, 1);
    static readonly SemaphoreSlim _ffAcquire = new SemaphoreSlim(1, 1);   // serialize the one-time ffmpeg fetch
    static volatile bool _ffTried;   // a failed ffmpeg fetch -> don't hammer the network every request this session
    static readonly SemaphoreSlim _dl = new SemaphoreSlim(3, 3);   // cap concurrent media downloads
    public static Action<string> OnLog;   // Engine wires this so the one-time ffmpeg fetch is visible in the log

    public readonly record struct Meta(
        bool Ok, string VideoId, int DurationSec, string Title, string Channel, bool IsLive, int AgeLimit, string Error);

    static readonly Regex IdRe = new Regex("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    // True only for an actual YouTube / YouTube Music LINK. Used by the request router - a bare
    // 11-char word must NOT be hijacked from Spotify search (e.g. "Rockstar123").
    public static bool IsLink(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s.IndexOf("youtube.com/", StringComparison.OrdinalIgnoreCase) >= 0
            || s.IndexOf("youtu.be/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Looser check (link OR a bare 11-char id) - used after an explicit "yt " prefix.
    public static bool LooksLikeYouTube(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return IsLink(s) || IdRe.IsMatch(s.Trim());
    }

    // Pull the 11-char video id out of any common YouTube URL form (watch, youtu.be, shorts, embed,
    // live, music.youtube.com), or accept a bare id. Returns null if none found.
    public static string ExtractId(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (IdRe.IsMatch(s)) return s;
        foreach (var pat in new[]
        {
            @"[?&]v=([A-Za-z0-9_-]{11})",
            @"youtu\.be/([A-Za-z0-9_-]{11})",
            @"youtube\.com/(?:shorts|embed|live|v)/([A-Za-z0-9_-]{11})",
        })
        {
            var m = Regex.Match(s, pat);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    // Ensure a reasonably-fresh yt-dlp.exe exists. Downloads the latest on demand; on failure falls
    // back to any existing copy. Safe to call before every resolve (cheap once present + fresh).
    static async Task<bool> EnsureAsync()
    {
        try { if (File.Exists(ExePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(ExePath) < RefreshAfter) return true; }
        catch { }
        await _acquire.WaitAsync();
        try
        {
            if (File.Exists(ExePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(ExePath) < RefreshAfter) return true;
            Directory.CreateDirectory(DataDir);
            var bytes = await Http.GetByteArrayAsync(DownloadUrl);
            string tmp = ExePath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, ExePath, true);
            return true;
        }
        catch { return File.Exists(ExePath); }   // keep working with whatever we already have
        finally { _acquire.Release(); }
    }

    // Ensure ffmpeg.exe exists (needed only to merge separate video+audio streams for HD video mode).
    // Fetched once from yt-dlp's own ffmpeg build and cached next to yt-dlp; survives restarts. Returns
    // false if it can't be obtained (caller then falls back to a single 360p progressive file).
    static async Task<bool> EnsureFfmpegAsync()
    {
        if (File.Exists(FfmpegPath)) return true;
        if (_ffTried) return false;
        await _ffAcquire.WaitAsync();
        try
        {
            if (File.Exists(FfmpegPath)) return true;
            if (_ffTried) return false;
            Directory.CreateDirectory(DataDir);
            OnLog?.Invoke("Fetching ffmpeg (one-time, ~150MB) so YouTube video can play in HD - this can take a minute…");
            string zip = Path.Combine(DataDir, "ffmpeg.zip.tmp");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })   // big file -> its own generous timeout
            {
                var bytes = await http.GetByteArrayAsync(FfmpegZipUrl);
                await File.WriteAllBytesAsync(zip, bytes);
            }
            using (var za = ZipFile.OpenRead(zip))
                foreach (var want in new[] { "ffmpeg.exe", "ffprobe.exe" })   // ffmpeg does the merge; ffprobe is a helper yt-dlp likes to have
                {
                    var entry = za.Entries.FirstOrDefault(e => string.Equals(e.Name, want, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) continue;
                    string dest = Path.Combine(DataDir, want), tmp = dest + ".tmp";
                    entry.ExtractToFile(tmp, true);
                    File.Move(tmp, dest, true);
                }
            try { File.Delete(zip); } catch { }
            if (File.Exists(FfmpegPath)) { OnLog?.Invoke("ffmpeg ready - HD YouTube video enabled."); return true; }
            _ffTried = true;
            OnLog?.Invoke("Couldn't unpack ffmpeg - YouTube video will fall back to 360p.");
            return false;
        }
        catch { _ffTried = true; OnLog?.Invoke("Couldn't fetch ffmpeg (offline?) - YouTube video will fall back to 360p."); return File.Exists(FfmpegPath); }
        finally { _ffAcquire.Release(); }
    }

    // Resolve a link/id to metadata (no search).
    public static async Task<Meta> ResolveAsync(string input)
    {
        string id = ExtractId(input);
        string target = id != null ? "https://www.youtube.com/watch?v=" + id : input.Trim();
        return await RunPrintAsync(target);
    }

    // Keyless YouTube search: first hit for the query.
    public static async Task<Meta> SearchAsync(string text)
        => await RunPrintAsync("ytsearch1:" + (text ?? "").Trim());

    static async Task<Meta> RunPrintAsync(string target)
    {
        if (!await EnsureAsync())
            return new Meta(false, null, 0, null, null, false, 0, "yt-dlp unavailable (offline?)");
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in new[]
            {
                "--no-warnings", "--skip-download", "--no-playlist", "--encoding", "utf-8",   // emit UTF-8 titles (– ' é …), not cp1252
                "--print", "%(id)s\t%(duration)s\t%(live_status)s\t%(age_limit)s\t%(channel)s\t%(title)s",
                target,
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            string outp = await p.StandardOutput.ReadToEndAsync();
            string err = await p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return new Meta(false, null, 0, null, null, false, 0, "timed out"); }
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(outp))
                return new Meta(false, null, 0, null, null, false, 0, Trim1(err) ?? "not found");

            var line = outp.Split('\n')[0].TrimEnd('\r');
            var f = line.Split('\t');
            if (f.Length < 6 || f[0].Length != 11)
                return new Meta(false, null, 0, null, null, false, 0, "unexpected yt-dlp output");
            int dur = int.TryParse(f[1], out var d) ? d : 0;         // 0/NA => live or unknown
            bool live = string.Equals(f[2], "is_live", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(f[2], "post_live", StringComparison.OrdinalIgnoreCase);
            int age = int.TryParse(f[3], out var a2) ? a2 : 0;
            return new Meta(true, f[0], dur, f[5], f[4], live, age, null);
        }
        catch (Exception e) { return new Meta(false, null, 0, null, null, false, 0, e.Message); }
    }

    static string Trim1(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var line = s.Split('\n')[0].Trim();
        return line.Length == 0 ? null : (line.Length > 160 ? line.Substring(0, 160) : line);
    }

    // ---- audio download (for local <audio> playback in the OBS overlay) ----

    // The cached audio file for this id, or null if not present. Ignores partial/temp files.
    public static string CachedPath(string id)
    {
        try
        {
            if (string.IsNullOrEmpty(id) || !Directory.Exists(AudioDir)) return null;
            foreach (var f in Directory.GetFiles(AudioDir, id + ".*"))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".part" || ext == ".tmp" || ext == ".ytdl") continue;
                return f;
            }
        }
        catch { }
        return null;
    }

    // Download a browser-playable file for a video id into the cache and return its local path (existing
    // or freshly downloaded), or null on failure. No transcoding either way (no ffmpeg needed):
    //   video=false -> one audio-only stream (m4a/webm) - small + fast, played behind the now-playing card.
    //   video=true  -> one PROGRESSIVE mp4 that already carries BOTH video and audio in a single file, so
    //                  there's no stream-merge step. Progressive tops out at 720p (itag 22) when YouTube
    //                  still offers it, otherwise 360p (itag 18); 1080p+ would require bundling ffmpeg.
    public static async Task<string> DownloadMediaAsync(string id, bool video)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var have = CachedPath(id);
        if (have != null) return have;
        if (!await EnsureAsync()) return null;
        await _dl.WaitAsync();
        try
        {
            have = CachedPath(id);
            if (have != null) return have;
            Directory.CreateDirectory(AudioDir);
            string outTmpl = Path.Combine(AudioDir, id + ".%(ext)s");
            bool haveFf = video && await EnsureFfmpegAsync();   // HD video needs ffmpeg to merge the separate video+audio streams
            string fmt = !video
                ? "bestaudio[ext=m4a]/bestaudio[ext=webm]/bestaudio"                                                       // audio-only: one stream, no merge
                : haveFf
                    ? "bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=1080][ext=mp4]+ba[ext=m4a]/b[ext=mp4]/b"  // up to 1080p H.264/AAC, merged -> plays in OBS CEF
                    : "best[ext=mp4][vcodec!=none][acodec!=none]/best[vcodec!=none][acodec!=none]/best";                      // no ffmpeg -> single progressive file (usually 360p)
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            var args = new List<string> { "--no-warnings", "--no-playlist", "--no-part", "--retries", "3", "-f", fmt, "-o", outTmpl };
            if (haveFf) { args.Add("--ffmpeg-location"); args.Add(DataDir); args.Add("--merge-output-format"); args.Add("mp4"); }   // mux the two streams into one <id>.mp4
            args.Add("https://www.youtube.com/watch?v=" + id);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            _ = p.StandardOutput.ReadToEndAsync();   // drain pipes so a chatty yt-dlp can't deadlock on a full buffer
            _ = p.StandardError.ReadToEndAsync();
            int timeoutMs = video ? 240000 : 120000;   // video (download + ffmpeg merge) is heavier -> allow longer
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } DeleteCached(id); return null; }   // killed mid-write -> remove the truncated file
            if (p.ExitCode != 0) { DeleteCached(id); return null; }
            return CachedPath(id);
        }
        catch { DeleteCached(id); return null; }
        finally { _dl.Release(); }
    }

    public static void DeleteCached(string id)
    {
        try { foreach (var f in Directory.GetFiles(AudioDir, id + ".*")) File.Delete(f); }
        catch { }
    }

    // Wipe the audio cache (called on engine start - the in-memory queue never survives a restart,
    // so any files left over are stale).
    public static void ClearAudioCache()
    {
        try { if (Directory.Exists(AudioDir)) Directory.Delete(AudioDir, true); }
        catch { }
    }
}
