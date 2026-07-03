using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

static class T
{
    public static readonly Color Bg = Color.FromArgb(22, 22, 28), Card = Color.FromArgb(38, 38, 48),
        Green = Color.FromArgb(29, 185, 84), GreenHi = Color.FromArgb(43, 210, 104),
        Gray = Color.FromArgb(150, 150, 165), GrayBtn = Color.FromArgb(56, 56, 70), GrayBtnHi = Color.FromArgb(74, 74, 92),
        Track = Color.FromArgb(50, 50, 62);
}

static class D
{
    public static GraphicsPath R(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) return p;               // nothing to draw
        int d = rad * 2; if (d > r.Width) d = r.Width; if (d > r.Height) d = r.Height;
        if (d <= 0) { p.AddRectangle(r); return p; }               // too small to round
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}

class RPanel : Panel
{
    public int Radius = 10;
    public RPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;                      // avoid GDI+ on 0/neg size
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
        using var p = D.R(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var b = new SolidBrush(BackColor); e.Graphics.FillPath(b, p);
    }
}

class RButton : Button
{
    public int Radius = 10; public Color Base = T.Green, Hover = T.GreenHi; bool _h;
    public RButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; ForeColor = Color.White; Cursor = Cursors.Hand;
        MouseEnter += (s, e) => { _h = true; Invalidate(); }; MouseLeave += (s, e) => { _h = false; Invalidate(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;                      // avoid GDI+ on 0/neg size
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
        using var p = D.R(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var b = new SolidBrush(Enabled ? (_h ? Hover : Base) : Color.FromArgb(70, 70, 82)); e.Graphics.FillPath(b, p);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// High-contrast, owner-drawn checkbox (the stock WinForms glyph is nearly invisible on dark bg).
class RCheck : Control
{
    public bool Checked = true;
    public RCheck()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand; ForeColor = Color.White; Height = 26;
        Click += (s, e) => { if (Enabled) { Checked = !Checked; Invalidate(); } };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : BackColor);
        const int bs = 20; int by = (Height - bs) / 2;
        var box = new Rectangle(1, by, bs, bs);
        using (var bp = D.R(box, 5))
        {
            using (var f = new SolidBrush(Checked ? T.Green : Color.FromArgb(48, 48, 60))) g.FillPath(f, bp);
            using (var pen = new Pen(Checked ? T.GreenHi : Color.FromArgb(130, 130, 150), 1.6f)) g.DrawPath(pen, bp);
        }
        if (Checked)
            using (var pen = new Pen(Color.White, 2.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new Point(box.X + 5, box.Y + 10), new Point(box.X + 9, box.Y + 14), new Point(box.X + 15, box.Y + 6) });
        TextRenderer.DrawText(g, Text, Font, new Rectangle(bs + 12, 0, Width - bs - 12, Height),
            Enabled ? ForeColor : T.Gray, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}

class Setup
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}

class SetupForm : Form
{
    // Everything downloads from the release-asset CDN (latest/download/...), never the rate-limited
    // GitHub API (60/hr per IP - a shared router can 403 it). manifest.json carries the versions.
    const string RepoDl = "https://github.com/Goku67HoodWars/Streamer.bot-Song-Requests/releases/latest/download/";
    static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SongRequests");
    // Fixed, standard per-user install location (like Discord / VS Code) - no prompt, no chance of
    // installing into a random folder the setup .exe happened to be sitting in.
    static readonly string StdInstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SongRequests");
    string PathFile => Path.Combine(AppData, "install.txt");
    string InstallDir;
    bool updateMode;

    RCheck chkDesktop; RButton btnInstall; Label lblStatus; RPanel track, fill;

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern int SendMessage(IntPtr h, int m, int w, int l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    void Drag() { try { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); } catch { } }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4); } catch { } }

    public SetupForm()
    {
        Directory.CreateDirectory(AppData);
        InstallDir = StdInstallDir;   // always the standard per-user location
        // "Update" (silent auto-run) only when the standard location already holds an install.
        updateMode = File.Exists(Path.Combine(StdInstallDir, "SongRequests.exe"))
                     || File.Exists(Path.Combine(StdInstallDir, "app", "SongRequests.exe"));
        BuildUi();
        if (updateMode) Shown += async (s, e) => await DoInstall();
    }

    void BuildUi()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = T.Bg; ForeColor = Color.White; Font = new Font("Segoe UI", 9.75f);
        Size = new Size(520, updateMode ? 176 : 288);

        var bar = new Panel { Left = 0, Top = 0, Width = Width, Height = 54, BackColor = T.Bg };
        bar.MouseDown += (s, e) => Drag();
        var head = new Label { Text = "Song Requests", Left = 24, Top = 15, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 14f) };
        head.MouseDown += (s, e) => Drag();
        var sub = new Label { Text = updateMode ? "Updater" : "Setup", Left = 26, Top = 40, AutoSize = true, ForeColor = T.Green, Font = new Font("Segoe UI", 8.5f) };
        sub.MouseDown += (s, e) => Drag();
        var close = new Label { Text = "✕", Left = Width - 42, Top = 14, Width = 28, Height = 26, TextAlign = ContentAlignment.MiddleCenter, ForeColor = T.Gray, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 11f) };
        close.MouseEnter += (s, e) => close.ForeColor = Color.FromArgb(240, 90, 90);
        close.MouseLeave += (s, e) => close.ForeColor = T.Gray;
        close.Click += (s, e) => Close();
        bar.Controls.AddRange(new Control[] { head, sub, close });
        Controls.Add(bar);

        int x = 24, w = 472, y;

        if (!updateMode)
        {
            y = 70;
            Controls.Add(new Label { Text = "Installs for your account and updates itself automatically", Left = x, Top = y, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 10f) });
            y += 24;
            Controls.Add(new Label { Text = "each time you open it. No settings to fiddle with.", Left = x, Top = y, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 10f) });
            y += 30;
            Controls.Add(new Label { Text = "Location:  " + StdInstallDir, Left = x, Top = y, AutoSize = true, ForeColor = Color.FromArgb(120, 120, 135), Font = new Font("Segoe UI", 8.5f) });
            y += 28;
            chkDesktop = new RCheck { Text = "Create a desktop shortcut", Checked = true, Left = x, Top = y, Width = w, Height = 26, BackColor = T.Bg, Font = new Font("Segoe UI", 9.75f) };
            Controls.Add(chkDesktop);
            y += 36;
            btnInstall = new RButton { Text = "Install", Left = x, Top = y, Width = w, Height = 46, Base = T.Green, Hover = T.GreenHi, Font = new Font("Segoe UI Semibold", 12f) };
            btnInstall.Click += async (s, e) => { InstallDir = StdInstallDir; await DoInstall(); };
            Controls.Add(btnInstall);
            y += 56;
        }
        else { y = 74; }

        lblStatus = new Label { Text = updateMode ? "Checking for updates..." : "", Left = x, Top = y, AutoSize = true, ForeColor = T.Gray, Font = new Font("Segoe UI", 9.5f) };
        Controls.Add(lblStatus);
        y += 26;
        track = new RPanel { Left = x, Top = y, Width = w, Height = 8, BackColor = T.Track, Radius = 4 };
        fill = new RPanel { Left = 0, Top = 0, Width = 0, Height = 8, BackColor = T.Green, Radius = 4 };
        track.Controls.Add(fill); Controls.Add(track);
    }

    async Task DoInstall()
    {
        try
        {
            if (btnInstall != null) btnInstall.Enabled = false;
            if (chkDesktop != null) chkDesktop.Enabled = false;

            if (string.IsNullOrWhiteSpace(InstallDir))
                InstallDir = StdInstallDir;
            Directory.CreateDirectory(AppData);
            File.WriteAllText(PathFile, InstallDir);
            Directory.CreateDirectory(InstallDir);

            Status("Checking for the latest version...");
            using var h = new HttpClient(); h.Timeout = TimeSpan.FromMinutes(20);
            h.DefaultRequestHeaders.Add("User-Agent", "SongRequests-Setup");
            // One CDN fetch for the version info; asset URLs are deterministic. No GitHub API involved.
            var m = JsonDocument.Parse(await h.GetStringAsync(RepoDl + "manifest.json")).RootElement;
            string tag = m.TryGetProperty("app", out var av) ? av.GetString() : null;
            if (string.IsNullOrEmpty(tag)) throw new Exception("Couldn't read the latest version info from GitHub.");
            string wantRt = m.TryGetProperty("runtime", out var rv) ? (rv.GetString() ?? "1") : "1";
            string appUrl = RepoDl + "app.zip", runtimeUrl = RepoDl + "runtime.zip";

            string appExe = Path.Combine(InstallDir, "SongRequests.exe");            // root launcher (from runtime.zip)
            string coreExe = Path.Combine(InstallDir, "app", "SongRequests.exe");     // real app (from app.zip)
            string verFile = Path.Combine(InstallDir, "app", "version.txt");          // markers live in app\
            string rtFile = Path.Combine(InstallDir, "app", "runtime.txt");
            string localApp = File.Exists(verFile) ? File.ReadAllText(verFile).Trim() : "";
            string localRt = File.Exists(rtFile) ? File.ReadAllText(rtFile).Trim() : "";
            bool haveRuntime = File.Exists(Path.Combine(InstallDir, "app", "coreclr.dll"));   // loose runtime present?
            bool needRuntime = !haveRuntime || localRt != wantRt;
            bool needApp = needRuntime || !File.Exists(coreExe) || !string.Equals(localApp, tag, StringComparison.OrdinalIgnoreCase);

            if (!needRuntime && !needApp) { Status("Up to date (" + tag + ")."); SetPct(1); }
            else
            {
                // Stop any running instances AND wait for them to actually exit, so their files unlock before we overwrite.
                foreach (var name in new[] { "SongRequests", "SongRequests-Start" })
                    foreach (var p in Process.GetProcessesByName(name))
                        try { p.Kill(true); p.WaitForExit(5000); } catch { }

                double appLo = 0;
                if (needRuntime)
                {
                    string rz = Path.Combine(Path.GetTempPath(), "sq_runtime.zip");
                    Status("Downloading runtime (one-time)...");
                    await Download(h, runtimeUrl, rz, 0.0, needApp ? 0.9 : 1.0);
                    Status("Installing runtime...");
                    await Task.Run(() => Extract(rz, InstallDir));   // throws if a file couldn't be written
                    File.WriteAllText(rtFile, wantRt);               // marker only after a fully-successful extract
                    try { File.Delete(rz); } catch { }
                    appLo = 0.9;
                }
                if (needApp)
                {
                    string az = Path.Combine(Path.GetTempPath(), "sq_app.zip");
                    Status("Downloading app...");
                    await Download(h, appUrl, az, appLo, 1.0);
                    Status("Installing...");
                    await Task.Run(() => Extract(az, InstallDir));
                    if (!File.Exists(coreExe)) throw new Exception("Install looks incomplete (app files missing). Please try again.");
                    File.WriteAllText(verFile, tag);
                    try { File.Delete(az); } catch { }
                }
                SetPct(1);
            }

            RegisterApp(InstallDir);   // Start-menu shortcut (searchable) + Add/Remove Programs entry
            // Desktop shortcut only when the user asked for one (fresh installs); never forced on update.
            if (chkDesktop != null && chkDesktop.Checked) CreateDesktopShortcut(InstallDir);

            Status("Launching...");
            string launch = File.Exists(appExe) ? appExe : coreExe;   // prefer the root launcher
            Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true, WorkingDirectory = InstallDir });
            await Task.Delay(700);
            Close();
        }
        catch (Exception e)
        {
            Status("Problem: " + e.Message);
            if (btnInstall != null) btnInstall.Enabled = true;
            if (chkDesktop != null) chkDesktop.Enabled = true;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    // Makes the app searchable (Start-menu shortcut) and a real entry in Windows "Apps & features".
    void RegisterApp(string installDir)
    {
        string exe = Path.Combine(installDir, "SongRequests.exe");
        string ico = Path.Combine(installDir, "icon.ico");
        string iconRef = File.Exists(ico) ? ico : exe;   // prefer a real .ico so Windows shows a crisp icon
        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Song Requests.lnk");
        try { File.Delete(lnk); } catch { }               // recreate fresh so the shell re-reads the icon
        try { CreateShortcut(lnk, exe, installDir, iconRef); }
        catch (Exception ex) { Status("Note: couldn't create the Start-menu shortcut (" + ex.Message + "). The app is still installed."); }
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SongRequests");
            if (k != null)
            {
                k.SetValue("DisplayName", "Song Requests");
                k.SetValue("DisplayIcon", iconRef);
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("InstallLocation", installDir);
                k.SetValue("Publisher", "Goku67HoodWars");
                k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                try { string vf = Path.Combine(installDir, "app", "version.txt"); if (!File.Exists(vf)) vf = Path.Combine(installDir, "version.txt"); if (File.Exists(vf)) { string v = File.ReadAllText(vf).Trim().TrimStart('v', 'V'); if (v.Length > 0) k.SetValue("DisplayVersion", v); } } catch { }
            }
        }
        catch { }
        try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }   // SHCNE_ASSOCCHANGED -> refresh shell icons
    }

    // Optional desktop shortcut (fresh installs only, when the user ticks the box).
    void CreateDesktopShortcut(string installDir)
    {
        try
        {
            string exe = Path.Combine(installDir, "SongRequests.exe");
            string ico = Path.Combine(installDir, "icon.ico");
            string iconRef = File.Exists(ico) ? ico : exe;
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Song Requests.lnk");
            try { File.Delete(lnk); } catch { }
            CreateShortcut(lnk, exe, installDir, iconRef);
        }
        catch { }   // a missing desktop shortcut is never worth failing the install over
    }

    // Creates the .lnk in-process via COM (no powershell dependency); falls back to powershell if COM is unavailable.
    static void CreateShortcut(string lnk, string exe, string installDir, string iconRef)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new InvalidOperationException("WScript.Shell unavailable");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exe });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { installDir });
                st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "Song Requests" });
                st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { iconRef + ",0" });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(sc);
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
            return;
        }
        catch
        {
            string ps = "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + lnk.Replace("'", "''") + "');"
                      + "$s.TargetPath='" + exe.Replace("'", "''") + "';$s.WorkingDirectory='" + installDir.Replace("'", "''") + "';"
                      + "$s.IconLocation='" + iconRef.Replace("'", "''") + ",0';$s.Description='Song Requests';$s.Save()";
            var p = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -WindowStyle Hidden -Command \"" + ps + "\"") { CreateNoWindow = true, UseShellExecute = false });
            if (p == null || !p.WaitForExit(8000) || p.ExitCode != 0) throw new Exception("shortcut helper failed");
        }
    }

    async Task Download(HttpClient h, string url, string dest, double lo = 0, double hi = 1)
    {
        using var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        using var s = await resp.Content.ReadAsStreamAsync();
        using var fs = File.Create(dest);
        var buf = new byte[81920]; long read = 0; int n;
        while ((n = await s.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            await fs.WriteAsync(buf, 0, n); read += n;
            if (total > 0) SetPct(lo + (hi - lo) * ((double)read / total));
            Status("Downloading... " + (read / 1024 / 1024) + (total > 0 ? " / " + (total / 1024 / 1024) : "") + " MB");
        }
        if (total > 0 && read < total) throw new Exception("The download was incomplete - please try again.");
    }

    // Extracts a zip (no top-folder prefix) over dest, overwriting, with a zip-slip guard.
    // Retries locked files briefly, then THROWS if anything couldn't be written (so we never
    // record a new version marker for an install that didn't fully apply).
    static void Extract(string zip, string dest)
    {
        string destFull = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
        var failed = new System.Collections.Generic.List<string>();
        using var za = ZipFile.OpenRead(zip);
        foreach (var e in za.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;   // directory entry
            string outP = Path.GetFullPath(Path.Combine(dest, e.FullName.Replace('/', '\\')));
            if (!outP.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip guard
            Directory.CreateDirectory(Path.GetDirectoryName(outP));
            bool ok = false;
            for (int attempt = 0; attempt < 12 && !ok; attempt++)
            {
                try { e.ExtractToFile(outP, true); ok = true; }
                catch (IOException) { System.Threading.Thread.Sleep(250); }                 // file still locked - wait + retry
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(250); }
                catch { break; }
            }
            if (!ok) failed.Add(e.FullName);
        }
        if (failed.Count > 0) throw new Exception("Some files were in use (is the app still running?). Please close it and try again.");
    }

    void Status(string t) { if (IsDisposed) return; try { if (InvokeRequired) BeginInvoke((Action)(() => lblStatus.Text = t)); else lblStatus.Text = t; } catch { } }
    void SetPct(double p) { if (IsDisposed) return; try { Action a = () => { fill.Width = (int)(track.Width * Math.Max(0, Math.Min(1, p))); }; if (InvokeRequired) BeginInvoke(a); else a(); } catch { } }
}
