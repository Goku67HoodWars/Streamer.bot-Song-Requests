using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

// ===== OBS config helpers (no WebSocket) =====
// The only two things the app touches in OBS's own config: add the queue dock to the [BasicWindow]
// ExtraBrowserDocks list (one-time, opt-in) and check whether OBS is currently running. OBS rewrites its
// config when it exits, so the dock edit only sticks while OBS is CLOSED - the caller checks IsObsRunning first.
// OBS 30.2+ moved [BasicWindow] (docks/layout) out of global.ini into user.ini; older OBS keeps it in
// global.ini. We target user.ini when it exists, else legacy global.ini. Writing the wrong file is invisible -
// OBS just ignores it - which is exactly the bug this replaced. (The audio SOURCE is handled by
// obs-autostart.lua now; nothing here creates sources.)
static class ObsConfig
{
    static string ObsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
    static string UserIni   => Path.Combine(ObsDir, "user.ini");
    static string GlobalIni => Path.Combine(ObsDir, "global.ini");
    // The file OBS actually reads docks from: user.ini on OBS 30.2+, else the legacy global.ini.
    static string DockIni => File.Exists(UserIni) ? UserIni : GlobalIni;

    // Match on host+path, not the full URL, so a stored scheme typo ("http:/...") or http-vs-https
    // still counts as "already present" and we don't add a duplicate.
    const string DockMarker = "127.0.0.1:8090/dock";

    public static bool ObsInstalled => File.Exists(UserIni) || File.Exists(GlobalIni);

    public static bool IsObsRunning()
    {
        try
        {
            foreach (var _ in Process.GetProcessesByName("obs64")) return true;
            foreach (var _ in Process.GetProcessesByName("obs32")) return true;
        }
        catch { }
        return false;
    }

    // Is our dock already registered? Lets the app show "already added" instead of adding a duplicate.
    public static bool DockPresent(string url) => DockPresentIn(DockIni);

    // Append the queue dock to [BasicWindow] ExtraBrowserDocks (idempotent). ONLY call while OBS is closed.
    // Returns true if it added one; false if it was already there or the write failed. This is a ONE-TIME
    // registration - after this OBS owns the dock like any the user added by hand; deleting it in OBS sticks.
    public static bool AddQueueDock(string title, string url) => AddQueueDockTo(DockIni, title, url);

    // --- testable cores (operate on an explicit ini path so they can be exercised against a temp file) ---

    internal static bool DockPresentIn(string ini)
    {
        try { return File.Exists(ini) && File.ReadAllText(ini).IndexOf(DockMarker, StringComparison.OrdinalIgnoreCase) >= 0; }
        catch { return false; }
    }

    internal static bool AddQueueDockTo(string ini, string title, string url)
    {
        try
        {
            if (!File.Exists(ini)) return false;
            var lines = new List<string>(File.ReadAllLines(ini));
            int idx = -1; string sect = null;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { sect = t; continue; }
                if (string.Equals(sect, "[BasicWindow]", StringComparison.OrdinalIgnoreCase)
                    && t.StartsWith("ExtraBrowserDocks=", StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            }
            string entry = "{\"title\": \"" + JEsc(title) + "\", \"url\": \"" + JEsc(url) + "\", \"uuid\": \"" + Guid.NewGuid().ToString("N") + "\"}";
            if (idx >= 0)
            {
                string cur = lines[idx].Substring(lines[idx].IndexOf('=') + 1).Trim();
                if (cur.IndexOf(DockMarker, StringComparison.OrdinalIgnoreCase) >= 0) return false;   // already added
                string inner = cur.Length >= 2 && cur.StartsWith("[") ? cur.Substring(1, cur.Length - 2).Trim() : "";
                lines[idx] = "ExtraBrowserDocks=[" + (inner.Length == 0 ? entry : inner + ", " + entry) + "]";
            }
            else
            {
                bool inserted = false;
                for (int i = 0; i < lines.Count; i++)
                    if (string.Equals(lines[i].Trim(), "[BasicWindow]", StringComparison.OrdinalIgnoreCase))
                    { lines.Insert(i + 1, "ExtraBrowserDocks=[" + entry + "]"); inserted = true; break; }
                if (!inserted)   // no [BasicWindow] section at all -> create it so the dock actually lands somewhere
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                    lines.Add("[BasicWindow]");
                    lines.Add("ExtraBrowserDocks=[" + entry + "]");
                }
            }
            File.WriteAllLines(ini, lines);
            return true;
        }
        catch { return false; }
    }

    static string JEsc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s) sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", _ => c.ToString() });
        return sb.ToString();
    }
}
