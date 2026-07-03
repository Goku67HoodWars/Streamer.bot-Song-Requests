using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

// ===== Launcher (root SongRequests.exe) =====
// Tiny self-contained stub that lives in the install ROOT. It just forwards whatever
// arguments it gets to the real app in  app\SongRequests.exe  (GUI / --engine / --stop /
// --uninstall / --shot), tells it where the root is via SQ_ROOT, and cleans up the old
// "everything dumped in root" layout the first time it runs after an update.
class Launcher
{
    static int Main(string[] args)
    {
        string root = AppContext.BaseDirectory.TrimEnd('\\');
        try { SelfMigrate(root); } catch { }

        // Force auto-update before launching the core: OBS opens -> runs us with --engine -> we pull the newest
        // release first, so everyone stays current with zero clicks. Only for the long-running launches
        // (GUI / --engine), never for --stop/--shot/--uninstall. Fully best-effort - any hiccup just launches
        // what's installed, and it never blocks longer than the short network timeout.
        bool longRun = args.Length == 0 || string.Equals(args[0], "--engine", StringComparison.OrdinalIgnoreCase);
        if (longRun) { try { AutoUpdate(root); } catch { } }

        string appDir = Path.Combine(root, "app");
        string core = Path.Combine(appDir, "SongRequests.exe");
        if (!File.Exists(core))
        {
            Msg("Song Requests couldn't find its program files.\r\n\r\nExpected:\r\n" + core +
                "\r\n\r\nTry reinstalling with SongRequests-Setup.exe.");
            return 1;
        }

        var psi = new ProcessStartInfo(core) { UseShellExecute = false, WorkingDirectory = appDir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SQ_ROOT"] = root;
        try { Process.Start(psi); }
        catch (Exception e) { Msg("Couldn't start Song Requests:\r\n" + e.Message); return 1; }
        return 0;   // the launcher exits immediately; the core keeps running on its own
    }

    // Everything comes from the release-asset CDN (latest/download/...), NEVER the GitHub API: the API is
    // rate-limited to 60 requests/hour per IP (an OBS launch on two PCs behind one router can burn through
    // it and start 403ing), while asset downloads are effectively unlimited. manifest.json carries both the
    // app tag and the runtime marker, and the asset URLs are deterministic - the API adds nothing.
    const string RepoDl = "https://github.com/Goku67HoodWars/Streamer.bot-Song-Requests/releases/latest/download/";

    // Pull the newest RELEASE before launching. App-only updates swap cleanly here because the core in app\
    // usually isn't running yet - and if it IS (OBS session live + a second launch, dock Restart), the
    // lock-probe below skips this round instead of half-extracting over loaded files. Runtime bumps (which
    // would replace THIS launcher, and it's running) are left to the in-app updater; we still apply the
    // app.zip side so the app itself never falls behind, and drop a flag so the GUI offers the big hop.
    // Serialized across processes with a mutex so two simultaneous launches can't extract over each other.
    static void AutoUpdate(string root)
    {
        string appDir = Path.Combine(root, "app");
        string verFile = Path.Combine(appDir, "version.txt");
        string localVer = ReadTrim(verFile);
        if (string.IsNullOrEmpty(localVer)) return;   // unknown local version -> never risk it

        using var mx = new Mutex(false, "Global\\SongRequestsLauncherUpdate");
        bool held = false; try { held = mx.WaitOne(TimeSpan.FromSeconds(20)); } catch (AbandonedMutexException) { held = true; } catch { }
        try
        {
            string tag, wantRt;
            using (var h = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })   // never hang OBS startup on the network
            {
                h.DefaultRequestHeaders.Add("User-Agent", "SongRequests-Launcher");
                var m = JsonDocument.Parse(h.GetStringAsync(RepoDl + "manifest.json").GetAwaiter().GetResult()).RootElement;
                tag = m.TryGetProperty("app", out var av) ? av.GetString() : null;
                wantRt = m.TryGetProperty("runtime", out var rv) ? (rv.GetString() ?? "1") : "1";
            }
            if (!RemoteNewer(localVer, tag)) return;   // up to date, or we're ahead (dev build) -> do nothing

            // Runtime bump? Can't self-replace the running launcher on launch -> flag it for the in-app
            // updater (the GUI offers the one-click big hop). The app.zip part still applies below.
            bool runtimePending = ReadTrim(Path.Combine(appDir, "runtime.txt")) != wantRt;
            if (runtimePending) { try { File.WriteAllText(Path.Combine(appDir, "update-pending.txt"), tag); } catch { } }

            // If the core is RUNNING (engine mid-session / GUI open), its files are loaded and locked -
            // extracting now would fail halfway and leave a mixed-version app\. Probe the DLL for an
            // exclusive lock and skip this round if anything holds it; the next quiet launch catches up.
            string coreDll = Path.Combine(appDir, "SongRequests.dll");
            if (File.Exists(coreDll))
            {
                try { using var probe = File.Open(coreDll, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch { return; }   // in use -> never risk a partial extract
            }

            // App update: download app.zip, extract over the install root (app\ tree + html assets), then
            // stamp version.txt (unless a runtime hop is still owed - the version isn't 'done' until then).
            string tmp = Path.Combine(Path.GetTempPath(), "sq_launcher_upd.zip");
            using (var h3 = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                h3.DefaultRequestHeaders.Add("User-Agent", "SongRequests-Launcher");
                File.WriteAllBytes(tmp, h3.GetByteArrayAsync(RepoDl + "app.zip").GetAwaiter().GetResult());
            }
            ZipFile.ExtractToDirectory(tmp, root, true);
            try { File.Delete(tmp); } catch { }
            try { File.WriteAllText(verFile, tag); } catch { }   // app.zip carries version.txt, but be certain
            // The runtime hop (if owed) is tracked separately: update-pending.txt stays, and the GUI's
            // check compares runtime.txt against the manifest itself - version.txt only tracks app bits.
            if (!runtimePending) { try { File.Delete(Path.Combine(appDir, "update-pending.txt")); } catch { } }
        }
        finally { if (held) { try { mx.ReleaseMutex(); } catch { } } }
    }

    static string ReadTrim(string f) { try { return File.Exists(f) ? File.ReadAllText(f).Trim() : ""; } catch { return ""; } }

    // True only when 'remote' is a strictly-newer version than 'local'. Both look like "v1.4.7". If either
    // can't be parsed as a dotted number, returns false - we never auto-update on ambiguity (no downgrades).
    static bool RemoteNewer(string local, string remote)
    {
        int[] a = ParseVer(local), b = ParseVer(remote);
        if (a == null || b == null) return false;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (y != x) return y > x;
        }
        return false;
    }
    static int[] ParseVer(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.TrimStart('v', 'V').Trim();
        var parts = v.Split('.');
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i]; int dash = p.IndexOf('-'); if (dash >= 0) p = p.Substring(0, dash);   // tolerate a "-suffix"
            if (!int.TryParse(p, out nums[i])) return null;
        }
        return nums;
    }

    // One old flat install (runtime + dlls dumped in root) -> clean root. Runs only when we see
    // flat leftovers (root\coreclr.dll) with the new layout already in place. To stay safe even if
    // someone installed into a shared folder, it deletes ONLY root items that have a same-named
    // counterpart in app\ (i.e. the stale flat copies of OUR files) - never the user's own files,
    // and never the root launcher itself.
    static void SelfMigrate(string root)
    {
        string appDir = Path.Combine(root, "app");
        if (!Directory.Exists(appDir)) return;                                    // new layout must be present
        if (!File.Exists(Path.Combine(appDir, "SongRequests.exe"))) return;
        if (!File.Exists(Path.Combine(root, "coreclr.dll"))) return;             // no flat leftovers -> nothing to do
        if (root.Length < 8) return;                                             // paranoia: not a drive root

        foreach (var f in Directory.GetFiles(appDir))
        {
            string name = Path.GetFileName(f);
            if (string.Equals(name, "SongRequests.exe", StringComparison.OrdinalIgnoreCase)) continue; // root copy is the launcher
            string rf = Path.Combine(root, name);
            if (File.Exists(rf)) { try { File.Delete(rf); } catch { } }
        }
        foreach (var d in Directory.GetDirectories(appDir))
        {
            string rd = Path.Combine(root, Path.GetFileName(d));
            if (Directory.Exists(rd)) PruneMirrored(rd, d);   // delete only OUR files inside, keep the user's
        }
        // pre-consolidation worker folder (only ever held our Start/Stop exes)
        string eng = Path.Combine(root, "engine");
        if (Directory.Exists(eng) &&
            (File.Exists(Path.Combine(eng, "SongRequests-Start.exe")) || File.Exists(Path.Combine(eng, "SongRequests-Stop.exe"))))
            { try { Directory.Delete(eng, true); } catch { } }
    }

    // Recursively delete, under rootDir, only the files that have a same-named counterpart under
    // appDir (the stale flat copies of OUR runtime files), then drop any directory this leaves
    // empty. A user folder that merely shares a name with a .NET locale folder (cs\ de\ fr\ ru\ ...)
    // is preserved: only exact same-named files are removed, and the folder survives if anything of
    // the user's remains. This mirrors the top-level same-name file rule so the whole migration is
    // safe even in a shared install root.
    static void PruneMirrored(string rootDir, string appDir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(rootDir))
                if (File.Exists(Path.Combine(appDir, Path.GetFileName(f))))
                    { try { File.Delete(f); } catch { } }
            foreach (var d in Directory.GetDirectories(rootDir))
            {
                string ad = Path.Combine(appDir, Path.GetFileName(d));
                if (Directory.Exists(ad)) PruneMirrored(d, ad);
            }
            if (Directory.Exists(rootDir) && Directory.GetFileSystemEntries(rootDir).Length == 0)
                { try { Directory.Delete(rootDir, false); } catch { } }   // only if OUR pruning emptied it
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
    static void Msg(string t) { try { MessageBoxW(IntPtr.Zero, t, "Song Requests", 0x10); } catch { } }
}
