using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// ===== Config GUI (SongRequests.exe) =====

static class Theme
{
    public static readonly Color Bg        = Color.FromArgb(22, 22, 28);
    public static readonly Color Card      = Color.FromArgb(38, 38, 48);
    public static readonly Color Green      = Color.FromArgb(29, 185, 84);
    public static readonly Color GreenHi    = Color.FromArgb(43, 210, 104);
    public static readonly Color Purple     = Color.FromArgb(160, 120, 255);
    public static readonly Color Gray       = Color.FromArgb(150, 150, 165);
    public static readonly Color GrayBtn    = Color.FromArgb(56, 56, 70);
    public static readonly Color GrayBtnHi  = Color.FromArgb(74, 74, 92);
    public static readonly Color Bad        = Color.FromArgb(232, 120, 120);
    public static readonly Color Side       = Color.FromArgb(25, 25, 33);
    public static readonly Color Main       = Color.FromArgb(19, 19, 25);
    public static readonly Color GreenDark  = Color.FromArgb(20, 150, 74);
}

static class Draw
{
    public static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) return p;               // nothing to draw
        int d = rad * 2; if (d > r.Width) d = r.Width; if (d > r.Height) d = r.Height;
        if (d <= 0) { p.AddRectangle(r); return p; }               // too small to round
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

class RoundPanel : Panel
{
    public int Radius = 12;
    public RoundPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;                      // avoid GDI+ on 0/neg size
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
        using var path = Draw.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var b = new SolidBrush(BackColor);
        e.Graphics.FillPath(b, path);
    }
}

class RoundButton : Button
{
    public int Radius = 12;
    public Color Base = Theme.Green, Hover = Theme.GreenHi;
    public Color? G2 = null;   // set for a gradient (Base -> G2)
    bool _h;
    static Color Lift(Color c) => Color.FromArgb(Math.Min(255, c.R + 16), Math.Min(255, c.G + 16), Math.Min(255, c.B + 16));
    public RoundButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; ForeColor = Color.White; Cursor = Cursors.Hand;
        MouseEnter += (s, e) => { _h = true; Invalidate(); };
        MouseLeave += (s, e) => { _h = false; Invalidate(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;                      // avoid GDI+ on 0/neg size
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
        using var path = Draw.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        if (Enabled && G2.HasValue)
        {
            using var b = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), _h ? Lift(Base) : Base, _h ? Lift(G2.Value) : G2.Value, 110f);
            e.Graphics.FillPath(b, path);
        }
        else
        {
            using var b = new SolidBrush(Enabled ? (_h ? Hover : Base) : Color.FromArgb(70, 70, 82));
            e.Graphics.FillPath(b, path);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// Rounded panel with a linear-gradient fill (used for the brand mark, nav highlight, status pills).
class GradientPanel : Panel
{
    public Color C1 = Color.Black, C2 = Color.Black;
    public float Angle = 115f;
    public int Radius = 0;
    public GradientPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : BackColor);
        using var br = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), C1, C2, Angle);
        if (Radius > 0) { using var p = Draw.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius); g.FillPath(br, p); }
        else g.FillRectangle(br, new Rectangle(0, 0, Width, Height));
    }
}

// Left-sidebar nav item: paints its own rounded gradient highlight when selected.
// A clip container with a thin, dark, rounded custom scrollbar (the default WinForms one looks out of place
// on a dark app). Content goes in .View; call Recalc() after you set View.Height. Wheel is routed by WheelFilter.
class ScrollHost : Panel
{
    public Panel View = new Panel { Left = 0, Top = 0, BackColor = Color.Transparent };
    readonly Panel thumb;
    bool drag; int dragMouseY, dragScroll;
    static readonly Color ThumbCol = Color.FromArgb(92, 92, 116), ThumbHot = Color.FromArgb(130, 130, 160);

    public ScrollHost()
    {
        SetStyle(ControlStyles.ResizeRedraw, true);
        Controls.Add(View);
        thumb = new Panel { Width = 9, BackColor = ThumbCol, Visible = false, Cursor = Cursors.Hand };
        thumb.MouseDown += (s, e) => { drag = true; dragMouseY = Cursor.Position.Y; dragScroll = ScrollY; thumb.BackColor = ThumbHot; };
        thumb.MouseMove += (s, e) =>
        {
            if (!drag || MaxScroll <= 0) return;
            int trackH = Height - 8, maxThumb = trackH - thumb.Height;
            if (maxThumb <= 0) return;
            int dy = Cursor.Position.Y - dragMouseY;
            SetScroll(dragScroll + (int)((long)dy * MaxScroll / maxThumb));
        };
        thumb.MouseUp += (s, e) => { drag = false; thumb.BackColor = ThumbCol; };
        thumb.MouseEnter += (s, e) => thumb.BackColor = ThumbHot;
        thumb.MouseLeave += (s, e) => { if (!drag) thumb.BackColor = ThumbCol; };
        Controls.Add(thumb);
    }

    public int ScrollY => -View.Top;
    public int MaxScroll => Math.Max(0, View.Height - Height);

    public void Recalc() { thumb.Visible = View.Height > Height; SetScroll(ScrollY); }

    public void SetScroll(int y)
    {
        y = Math.Max(0, Math.Min(y, MaxScroll));
        View.Top = -y;
        if (MaxScroll > 0)
        {
            int trackH = Height - 8;
            int th = Math.Max(40, (int)((float)Height / View.Height * trackH));
            int ty = 4 + (MaxScroll > 0 ? (int)((float)y / MaxScroll * (trackH - th)) : 0);
            thumb.SetBounds(Width - thumb.Width - 5, ty, thumb.Width, th);
            try { using var p = Draw.Round(new Rectangle(0, 0, thumb.Width, th), thumb.Width / 2); thumb.Region = new Region(p); } catch { }
            thumb.BringToFront();
        }
    }

    public void WheelScroll(int delta) => SetScroll(ScrollY - Math.Sign(delta) * 70);
}

// Routes the mouse wheel to whichever ScrollHost the cursor is over, regardless of which child has focus
// (WinForms otherwise sends the wheel only to the focused control).
class WheelFilter : System.Windows.Forms.IMessageFilter
{
    readonly ScrollHost host;
    public WheelFilter(ScrollHost h) { host = h; }
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x20A || host == null || !host.IsHandleCreated || !host.Visible) return false;
        try
        {
            if (!host.RectangleToScreen(host.ClientRectangle).Contains(Cursor.Position)) return false;
            host.WheelScroll((short)((long)m.WParam >> 16));
            return true;
        }
        catch { return false; }
    }
}

class ConfigApp
{
    [STAThread]
    static void Main(string[] args)
    {
        // Headless modes (one exe): the queue engine + its remote stop signal.
        if (args != null && args.Length > 0)
        {
            if (string.Equals(args[0], "--engine", StringComparison.OrdinalIgnoreCase)) { Engine.RunEngine().GetAwaiter().GetResult(); return; }
            if (string.Equals(args[0], "--stop", StringComparison.OrdinalIgnoreCase)) { Engine.SignalStop(); return; }
            if (string.Equals(args[0], "--fix-obs-admin", StringComparison.OrdinalIgnoreCase)) { ConfigForm.PatchSystemObs(); return; }   // elevated: patch the all-users OBS shortcut
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args != null && args.Length > 0 && string.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
        { ConfigForm.UninstallHeadless(); return; }   // invoked from Windows "Apps & features" -> Uninstall
        if (args != null && args.Length > 1 && args[0] == "--shot")   // dev: render the UI to a PNG
        {
            var f = new ConfigForm(); f.Show();
            if (args.Length > 2 && int.TryParse(args[2], out int vi)) f.SelectView(vi);
            Application.DoEvents(); System.Threading.Thread.Sleep(500); Application.DoEvents();
            using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(args[1]); }
            f.Close(); return;
        }
        // Single-instance GUI: if a window is already open, wave it to the front and exit instead of
        // opening a second copy. A duplicate window is confusing, and a second SongRequests.exe would
        // also wedge the update/uninstall step that waits for every SongRequests.exe to close.
        bool guiNew;
        using var guiLock = new System.Threading.Mutex(true, "SongRequestsGuiSingleton_v1", out guiNew);
        if (!guiNew)
        {
            try { using var ev = System.Threading.EventWaitHandle.OpenExisting("SongRequestsGuiShow_v1"); ev.Set(); } catch { }
            return;
        }
        var showEvt = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "SongRequestsGuiShow_v1");
        var mainForm = new ConfigForm();
        var waker = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try { showEvt.WaitOne(); } catch { break; }
                try { if (!mainForm.IsDisposed) mainForm.BeginInvoke(new Action(mainForm.SurfaceWindow)); } catch { }
            }
        }) { IsBackground = true };
        // Start the waker only once the window handle exists so BeginInvoke can't miss an early
        // signal; a second launch that fires before Load is latched by the auto-reset event and
        // delivered on the first WaitOne.
        mainForm.Load += (s, e) => { try { waker.Start(); } catch { } };
        Application.Run(mainForm);
    }
}

class ConfigForm : Form
{
    static readonly HttpClient Http = new HttpClient();
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SongRequests");
    string ConfigFile => Path.Combine(DataDir, "config.txt");
    string TokenFile  => Path.Combine(DataDir, "token.txt");
    // The core runs from <install>\app; InstallRoot is where the launcher + overlay + shortcut live.
    static string InstallRoot()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("SQ_ROOT");
            if (!string.IsNullOrEmpty(env) && LooksLikeInstall(env)) return env.TrimEnd('\\');
            string b = AppContext.BaseDirectory.TrimEnd('\\');
            if (string.Equals(Path.GetFileName(b), "app", StringComparison.OrdinalIgnoreCase))
            {
                var p = Directory.GetParent(b);
                if (p != null && File.Exists(Path.Combine(p.FullName, "SongRequests.exe")))
                    return p.FullName.TrimEnd('\\');   // real install: the parent holds our launcher
            }
            return b;   // dev / flat layout, or an unexpected tree: stay put, never walk up to a stranger's folder
        }
        catch { return AppContext.BaseDirectory.TrimEnd('\\'); }
    }
    // A directory only counts as our install root if it actually holds our launcher (or the app\ core).
    // Guards the destructive paths (uninstall rmdir, in-app update extract) against a stale SQ_ROOT env
    // var or an odd layout pointing somewhere that isn't ours.
    static bool LooksLikeInstall(string dir)
        => Directory.Exists(dir)
           && (File.Exists(Path.Combine(dir, "SongRequests.exe")) || File.Exists(Path.Combine(dir, "app", "SongRequests.exe")));
    string OverlayFile => Path.Combine(InstallRoot(), "nowplaying.html");
    // Version checks + downloads go through the release-asset CDN (latest/download/...), NEVER the GitHub
    // API: the API is rate-limited to 60/hr per IP (two PCs + one OBS launch each can exhaust it and 403),
    // the CDN isn't. manifest.json carries both the app tag and the runtime marker - all we need.
    const string RepoDl = "https://github.com/Goku67HoodWars/Streamer.bot-Song-Requests/releases/latest/download/";
    string AppVersion { get { try { var f = Path.Combine(AppContext.BaseDirectory, "version.txt"); return File.Exists(f) ? File.ReadAllText(f).Trim() : "dev build"; } catch { return "dev build"; } } }

    string ClientId, ClientSecret, RedirectUri;
    int RedirectPort = 8888;

    const string WsHelp =
        "In Streamer.bot:\r\n\r\n" +
        "1) Left sidebar  ->  Servers/Clients  ->  WebSocket Server\r\n" +
        "2) Auto Start:  On\r\n" +
        "3) Address:  127.0.0.1\r\n" +
        "4) Port:  8080\r\n" +
        "5) Endpoint:  /\r\n" +
        "6) Authentication:  Disabled\r\n" +
        "7) Server Status must say \"Running\" (click Start Server if not)\r\n\r\n" +
        "Leave Streamer.bot open while you stream.";

    const string SpotifyHelp =
        "Get your Spotify keys (one time, ~3 min):\r\n\r\n" +
        "1) Go to  developer.spotify.com/dashboard  and log in\r\n" +
        "2) Click \"Create app\". Name it anything.\r\n" +
        "3) In \"Redirect URIs\" paste EXACTLY:\r\n" +
        "       http://127.0.0.1:8888/callback\r\n" +
        "4) Tick \"Web API\", then Save.\r\n" +
        "5) Open the app  ->  Settings  ->  copy the Client ID and Secret\r\n" +
        "6) Paste them into the fields above, then click Connect Spotify.\r\n\r\n" +
        "Note: Spotify PREMIUM is required to queue songs.";

    TextBox txtId, txtSecret, txtReward, txtWs, txtLog;
    RoundButton btnConnect;
    RoundPanel statusPill;
    Label lblSpotify;
    RoundPanel infoPanel; Label infoTitle, infoBody;
    RoundPanel confirmPanel; Label confirmTitle, confirmBody; RoundButton confirmYes, confirmNo;
    Action pendingConfirm;
    RoundPanel enginePill; Label lblEngine; RoundButton btnQueue; System.Windows.Forms.Timer engineTimer;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunVal = "SongRequests";
    ScrollHost page;   // the single scrolling setup page - the whole app (custom dark scrollbar)
    // First-run wizard: live checklist state (dots + detail lines), polled by the engine timer.
    Label[] wizDot, wizDetail; RoundPanel wizDonePanel; Label wizDoneLbl;
    volatile bool _wizProbeBusy; string _wizTok;   // cached engine control token for the /queue probe

    public ConfigForm()
    {
        Directory.CreateDirectory(DataDir);
        BuildUi();
        LoadConfig();
        EnsureOverlay();   // make sure nowplaying.html exists (materialized from nowplaying-default.html) for the overlay
        try { if (File.Exists(TokenFile) && File.ReadAllText(TokenFile).Trim().Length > 0) SetSpotify(true, "already connected"); } catch { }
    }

    // rounded window + drag
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern int SendMessage(IntPtr h, int m, int w, int l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    void Drag() { try { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); } catch { } }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4); } catch { } }
    protected override void OnFormClosed(FormClosedEventArgs e) { try { engineTimer?.Stop(); engineTimer?.Dispose(); } catch { } base.OnFormClosed(e); }

    void BuildUi()
    {
        FormBorderStyle = FormBorderStyle.None;
        int formH = 940;   // big, easy-to-read one-time-setup window
        if (int.TryParse(Environment.GetEnvironmentVariable("SQ_SHOT_H"), out var shotH) && shotH > 400) formH = shotH;   // dev: --shot the whole tall page in one image
        Size = new Size(1180, formH);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Main; ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);
        Text = "Song Requests";   // taskbar / alt-tab label (the window itself is borderless)
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        RoundCorners();

        // ---- top bar: brand + window buttons (the ONLY chrome; everything else is the one setup page) ----
        var bar = new Panel { Left = 0, Top = 0, Width = Width, Height = 64, BackColor = Theme.Side };
        bar.MouseDown += (s, e) => Drag();
        var logo = new PictureBox { Left = 22, Top = 16, Width = 32, Height = 32, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Side };
        try { logo.Image = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)?.ToBitmap(); } catch { }
        logo.MouseDown += (s, e) => Drag();
        var brand = new Label { Left = 62, Top = 11, AutoSize = true, Text = "Song Requests", Font = new Font("Segoe UI Semibold", 12.5f), ForeColor = Color.White, BackColor = Theme.Side };
        brand.MouseDown += (s, e) => Drag();
        var sub = new Label { Left = 64, Top = 37, AutoSize = true, Text = "Setup  ·  updates install by themselves when you open the app or OBS", Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(130, 132, 146), BackColor = Theme.Side };
        sub.MouseDown += (s, e) => Drag();
        var ver = new Label { Left = Width - 132, Top = 40, AutoSize = true, Text = AppVersion, Font = new Font("Segoe UI", 8.25f), ForeColor = Color.FromArgb(105, 107, 120), BackColor = Theme.Side };
        ver.MouseDown += (s, e) => Drag();
        var close = WinBtn("✕", Width - 44); close.Top = 16; close.Click += (s, e) => Close(); close.MouseEnter += (s, e) => close.ForeColor = Color.FromArgb(240, 90, 90);
        var min = WinBtn("—", Width - 82); min.Top = 16; min.Click += (s, e) => WindowState = FormWindowState.Minimized;
        bar.Controls.AddRange(new Control[] { logo, brand, sub, ver, close, min });
        Controls.Add(bar);

        // ---- the single scrolling setup page ----
        // Build the content at a fixed "design" width, then Scale() the whole thing up so fonts + spacing get
        // uniformly bigger (easier to read) with the proportions preserved - no per-control retuning.
        const float S = 1.28f;
        const int designW = 900;
        page = new ScrollHost { Left = 0, Top = 64, Width = Width, Height = Height - 64, BackColor = Theme.Main };
        page.View.Width = designW;
        int contentH = BuildSetupView(page.View);
        page.View.Height = contentH;
        page.View.Scale(new SizeF(S, S));                                   // scales child bounds AND fonts
        page.View.Left = Math.Max(0, (page.Width - page.View.Width) / 2);   // centered column
        page.Recalc();
        Controls.Add(page);
        Application.AddMessageFilter(new WheelFilter(page));                // wheel scrolls on hover, any focus

        BuildInfoOverlay();
        BuildConfirmOverlay();

        engineTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        engineTimer.Tick += (s, e) => { UpdateEngineStatus(); ProbeWizard(); };
        engineTimer.Start();
        UpdateEngineStatus();
        ProbeWizard();
        MaybeAutoUpdate();   // no "check for updates" button: any pending update just installs on open
    }

    void RoundCorners() { try { using var p = Draw.Round(new Rectangle(0, 0, Width, Height), 16); Region = new Region(p); } catch { } }

    // Bring the window to the foreground when a second launch is attempted (single-instance).
    public void SurfaceWindow()
    {
        try
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            TopMost = true; TopMost = false;   // nudge past Windows' foreground-stealing rules
            Activate(); BringToFront();
        }
        catch { }
    }

    // Single-page app now. Kept as a no-op so old callers (ConnectSpotify, the --shot dev mode) still compile;
    // it just scrolls the setup page back to the top.
    public void SelectView(int i) { try { page?.SetScroll(0); } catch { } }

    // The WHOLE app: one scrolling setup page, top to bottom, that tells you exactly what to do and turns
    // each step green on its own as you finish it. Spotify is the only optional step.
    int BuildSetupView(Panel v)
    {
        var fTitle = new Font("Segoe UI Semibold", 12f);
        var fBody  = new Font("Segoe UI", 9.5f);
        var fNote  = new Font("Segoe UI", 8.5f);
        var fField = new Font("Segoe UI", 11f);
        var fBtn   = new Font("Segoe UI Semibold", 10f);
        var fChip  = new Font("Segoe UI Semibold", 9f);
        var fLbl   = new Font("Segoe UI", 8.5f);
        Color cBody = Color.FromArgb(202, 204, 216), cNote = Color.FromArgb(150, 152, 166), cInner = Color.FromArgb(30, 30, 39);

        int W = Math.Min(812, v.Width - 96), y = 18;
        int X = Math.Max(24, (v.Width - W) / 2);   // centered content column
        wizDot = new Label[5]; wizDetail = new Label[5];

        Label L(Control p, int lx, int ly, int lw, int lh, string t, Color col, Font f)
        { var l = new Label { Left = lx, Top = ly, Width = lw, Height = lh, Text = t, ForeColor = col, Font = f, BackColor = Color.Transparent }; p.Controls.Add(l); return l; }
        RoundPanel Card(int h) { var p = new RoundPanel { Left = X, Top = y, Width = W, Height = h, BackColor = Theme.Card }; v.Controls.Add(p); y += h + 14; return p; }
        void Dot(Control p, int idx, int top)
        { wizDot[idx] = new Label { Left = 16, Top = top, Width = 20, Height = 20, Text = "●", ForeColor = Theme.Gray, Font = new Font("Segoe UI", 12f), BackColor = Color.Transparent }; p.Controls.Add(wizDot[idx]); }
        RoundButton Btn(Control p, int bx, int by, int bw, int bh, string t, Color baseC, Color hov, Color? g2)
        { var b = new RoundButton { Left = bx, Top = by, Width = bw, Height = bh, Text = t, Base = baseC, Hover = hov, G2 = g2, Font = fBtn }; p.Controls.Add(b); return b; }
        TextBox Field(Control p, int fx, int fy, int fw, bool pw)
        { var pan = new RoundPanel { Left = fx, Top = fy, Width = fw, Height = 38, BackColor = cInner };
          var tb = new TextBox { Left = 12, Top = 9, Width = fw - 24, BorderStyle = BorderStyle.None, BackColor = cInner, ForeColor = Color.White, Font = fField }; if (pw) tb.UseSystemPasswordChar = true;
          pan.Controls.Add(tb); p.Controls.Add(pan); return tb; }

        L(v, X, y, W, 30, "Set up Song Requests", Color.White, new Font("Segoe UI Semibold", 15f)); y += 34;
        L(v, X, y, W, 22, "Do the steps top to bottom. Each turns green on its own when it's done — only Spotify is optional.", cNote, fBody); y += 32;

        // ---------- STEP 1: Spotify (optional) ----------
        {
            var c = Card(378);
            Dot(c, 2, 16);
            string t1 = "1    Connect Spotify";
            int t1w = TextRenderer.MeasureText(t1, fTitle).Width;
            L(c, 44, 12, t1w + 10, 26, t1, Color.White, fTitle);
            L(c, 44 + t1w + 14, 16, W - 60 - t1w - 14, 22, "—  OPTIONAL: leave empty if you only want YouTube requests", Theme.Purple, fBody);
            wizDetail[2] = L(c, 44, 38, W - 60, 18, "checking…", cNote, fNote);
            L(c, 20, 62, W - 40, 40, "Only if you want viewers to request Spotify songs (needs Spotify Premium). Leave the boxes empty to run YouTube-only — viewers just type a song name or paste a YouTube link. YouTube always works.", cBody, fBody);

            var box = new RoundPanel { Left = 20, Top = 106, Width = W - 40, Height = 110, BackColor = cInner };
            L(box, 14, 10, W - 68, 18, "Get your Spotify keys (about 3 minutes):", Theme.Green, new Font("Segoe UI Semibold", 9f));
            L(box, 14, 32, W - 68, 74,
                "1.  Open developer.spotify.com/dashboard and log in\r\n" +
                "2.  Click “Create app” — name it anything\r\n" +
                "3.  Redirect URI — paste EXACTLY:  http://127.0.0.1:8888/callback\r\n" +
                "4.  Tick “Web API”, then Save\r\n" +
                "5.  Open it → Settings → copy the Client ID + Secret below", cBody, fNote);
            c.Controls.Add(box);
            var bOpen = Btn(c, 20, 224, 220, 34, "🌐  Open dashboard", Theme.GrayBtn, Theme.GrayBtnHi, null);
            bOpen.Click += (s, e) => OpenUrl("https://developer.spotify.com/dashboard");
            var bCopy = Btn(c, 250, 224, 250, 34, "📋  Copy redirect URI", Theme.GrayBtn, Theme.GrayBtnHi, null);
            bCopy.Click += (s, e) => { try { Clipboard.SetText("http://127.0.0.1:8888/callback"); } catch { } Log("Redirect URI copied — paste it into your Spotify app's Redirect URIs."); };

            L(c, 20, 266, 120, 18, "Client ID", cNote, fLbl);
            L(c, 20 + (W - 52) / 2 + 12, 266, 120, 18, "Client Secret", cNote, fLbl);
            txtId = Field(c, 20, 284, (W - 52) / 2, false);
            txtSecret = Field(c, 20 + (W - 52) / 2 + 12, 284, (W - 52) / 2, true);

            btnConnect = new RoundButton { Left = 20, Top = 330, Width = 200, Height = 38, Text = "Connect Spotify", Base = Theme.Green, G2 = Theme.GreenDark, Font = fBtn };
            btnConnect.Click += async (s, e) => await ConnectSpotify();
            c.Controls.Add(btnConnect);
            statusPill = new RoundPanel { Left = 232, Top = 330, Width = W - 252, Height = 38, BackColor = Color.FromArgb(40, 28, 28) };
            lblSpotify = new Label { Text = "   ●   Spotify: not connected", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Bad, Font = fBody, BackColor = Color.Transparent };
            statusPill.Controls.Add(lblSpotify); c.Controls.Add(statusPill);
        }

        // ---------- STEP 2: create the reward IN Streamer.bot ----------
        {
            var c = Card(342);
            Dot(c, 3, 16);
            L(c, 44, 12, W - 60, 24, "2    Create your reward — in Streamer.bot", Color.White, fTitle);
            wizDetail[3] = L(c, 44, 38, W - 60, 18, "checking…", cNote, fNote);
            L(c, 20, 62, W - 40, 42, "Make the channel-point reward INSIDE Streamer.bot — not on the Twitch website. Only the app that creates a reward is allowed to refund points or pause it, so a reward made on Twitch can't be auto-refunded.", cBody, fBody);

            int bx = 20, by = 110;
            void Chip(string t) { int wc = TextRenderer.MeasureText(t, fChip).Width + 22; var pp = new RoundPanel { Left = bx, Top = by, Width = wc, Height = 30, BackColor = Color.FromArgb(46, 40, 72) };
                pp.Controls.Add(new Label { Dock = DockStyle.Fill, Text = t, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(190, 172, 255), Font = fChip, BackColor = Color.Transparent }); c.Controls.Add(pp); bx += wc; }
            void Arr() { c.Controls.Add(new Label { Left = bx, Top = by, Width = 18, Height = 30, Text = "›", TextAlign = ContentAlignment.MiddleCenter, ForeColor = cNote, Font = new Font("Segoe UI", 12f), BackColor = Color.Transparent }); bx += 18; }
            Chip("Platforms"); Arr(); Chip("Twitch"); Arr(); Chip("Channel Point Rewards"); Arr(); Chip("＋ New");

            L(c, 20, 150, W - 40, 92,
                "1.  In Streamer.bot's left sidebar:  Platforms → Twitch → Channel Point Rewards\r\n" +
                "2.  Click ＋ (or “New”) to add a reward\r\n" +
                "3.  Name it  (for example:  Song Request)\r\n" +
                "4.  Turn ON “Require viewer to enter text”   ← lets viewers type the song\r\n" +
                "5.  Set a cost, then Save", cBody, fBody);

            L(c, 20, 250, 300, 18, "Now type that EXACT reward name here:", cNote, fLbl);
            txtReward = Field(c, 20, 270, W - 40, false);
            L(c, 20, 312, W - 40, 18, "Must match the Twitch reward name exactly — spelling, spaces and capitals.", cNote, fNote);
        }

        // ---------- STEP 3: WebSocket server ----------
        {
            var c = Card(228);
            Dot(c, 0, 16);
            L(c, 44, 12, W - 60, 24, "3    Turn on Streamer.bot's WebSocket Server", Color.White, fTitle);
            wizDetail[0] = L(c, 44, 38, W - 60, 18, "checking…", cNote, fNote);
            L(c, 20, 62, W - 40, 22, "This is how the app hears your redemptions. In Streamer.bot:", cBody, fBody);
            L(c, 20, 86, W - 40, 60,
                "Servers/Clients → WebSocket Server →  Auto Start: ON,  Address: 127.0.0.1,  Port: 8080,\r\n" +
                "Endpoint: /,  Authentication: Off.   The status must say “Running”.", cBody, fNote);
            L(c, 20, 152, 300, 18, "WebSocket address (leave as-is)", cNote, fLbl);
            txtWs = Field(c, 20, 172, W - 40 - 100, false);
            var bSave = new RoundButton { Left = W - 20 - 90, Top = 172, Width = 90, Height = 38, Text = "Save", Base = Theme.Green, G2 = Theme.GreenDark, Font = fBtn };
            bSave.Click += (s, e) => { SaveConfig(); Log("Settings saved."); ProbeWizard(); };
            c.Controls.Add(bSave);
        }

        // ---------- STEP 4: OBS script ----------
        {
            var c = Card(266);
            Dot(c, 1, 16);
            L(c, 44, 12, 340, 24, "4    Set up OBS — one script does it all", Color.White, fTitle);
            wizDetail[1] = L(c, 44, 38, W - 240, 18, "checking…", cNote, fNote);
            enginePill = new RoundPanel { Left = W - 20 - 150, Top = 10, Width = 150, Height = 26, BackColor = Color.FromArgb(40, 28, 28) };
            lblEngine = new Label { Dock = DockStyle.Fill, Text = "  ● Queue: stopped", TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Bad, Font = fNote, BackColor = Color.Transparent };
            enginePill.Controls.Add(lblEngine); c.Controls.Add(enginePill);
            var bStart = new RoundButton { Left = W - 20 - 150 - 84, Top = 10, Width = 76, Height = 26, Text = "▶ Start", Base = Theme.GrayBtn, Hover = Theme.GrayBtnHi, Font = fNote };
            bStart.Click += (s, e) => { StartEngine(); ProbeWizard(); };
            c.Controls.Add(bStart);
            btnQueue = new RoundButton { Visible = false };   // kept only so UpdateEngineStatus has something to poke; starting is automatic via the script

            L(c, 20, 62, W - 40, 42, "This one script starts the engine when OBS opens, creates the invisible “YouTube Player” audio source, and keeps the app updated. No manual source, no audio settings to get wrong.", cBody, fBody);
            L(c, 20, 108, W - 40, 60,
                "1.  Click “Copy OBS script path” (below).\r\n" +
                "2.  In OBS:  Tools → Scripts → the ＋ button (bottom-left).\r\n" +
                "3.  Click the “Script Path / File name” box, paste (Ctrl+V), then Open. Leave it there.", cBody, fBody);
            var bLua = Btn(c, 20, 174, 214, 38, "📋  Copy OBS script path", Theme.GrayBtn, Theme.GrayBtnHi, null);
            bLua.Click += (s, e) => { try { Clipboard.SetText(Path.Combine(InstallRoot(), "obs-autostart.lua")); } catch { } Log("OBS script path copied — in OBS: Tools → Scripts → ＋ → paste → Open."); };
            var bSnd = Btn(c, 246, 174, 232, 38, "🔊  Fix YouTube sound in OBS", Theme.GrayBtn, Theme.GrayBtnHi, null);
            bSnd.Click += (s, e) => FixObsAutoplay();
            L(c, 20, 218, W - 40, 34, "“Fix YouTube sound” is a one-time click — OBS blocks autoplay audio until then. After clicking it, fully close OBS (tray icon → Exit) and reopen it.", cNote, fNote);
        }

        // ---------- STEP 5: control dock ----------
        {
            var c = Card(244);
            Dot(c, 4, 16);
            L(c, 44, 12, W - 60, 24, "5    Add the control dock", Color.White, fTitle);
            wizDetail[4] = L(c, 44, 38, W - 60, 18, "checking…", cNote, fNote);
            L(c, 20, 62, W - 40, 42, "The dock lives inside OBS and is your control panel: requests on/off, volume, max song length, play / pause / skip, and the live queue. Everything runs from here once you're live.", cBody, fBody);
            L(c, 20, 106, W - 40, 22, "Add it — close OBS first (OBS overwrites its layout when it closes), then click:", cNote, fNote);
            var bDock = new RoundButton { Left = 20, Top = 132, Width = 240, Height = 38, Text = "➕  Add the dock to OBS", Base = Theme.Green, G2 = Theme.GreenDark, Font = fBtn };
            bDock.Click += (s, e) => AddQueueDockClicked();
            c.Controls.Add(bDock);
            L(c, 20, 182, W - 40, 46, "Then show it — reopen OBS, click “Docks” in the top-left menu bar, and turn ON “Song Queue”. Drag it anywhere you like. (If you added it by hand it's there too.)", cBody, fBody);
        }

        // ---------- Optional extras ----------
        {
            var c = Card(228);
            L(c, 20, 12, W - 40, 24, "Optional extras", Color.White, fTitle);
            L(c, 20, 46, W - 40, 22, "Chat replies — the app posts “@viewer ✅ queued: Song” for each request (turn it on in the dock).", cBody, fBody);
            var bChat = Btn(c, 20, 74, 480, 36, "💬  Auto chat-reply: click this, then import into Streamer.bot", Theme.GrayBtn, Theme.GrayBtnHi, null);
            bChat.Click += (s, e) => ShowChatSetup();
            L(c, 20, 126, W - 40, 22, "Now-Playing overlay — show the current song on stream. Use the built-in one, or design your own.", cBody, fBody);
            var bOv = Btn(c, 20, 154, 292, 36, "🖥  Add the built-in overlay", Theme.GrayBtn, Theme.GrayBtnHi, null);
            bOv.Click += (s, e) => { EnsureOverlay(); try { Clipboard.SetText(OverlayFile); } catch { } ShowInfo("Now Playing overlay in OBS", ObsHelp()); };
            var bCustom = Btn(c, 324, 154, W - 324 - 20, 36, "🎨  Make your own — custom overlay guide", Theme.GrayBtn, Theme.GrayBtnHi, null);
            bCustom.Click += (s, e) => ShowCustomOverlay();
            L(c, 20, 196, W - 40, 22, "The guide opens on GitHub — it lists the live data + a prompt to have an AI build the HTML, then where to save it.", cNote, fNote);
        }

        // ---------- ready banner ----------
        wizDonePanel = new RoundPanel { Left = X, Top = y, Width = W, Height = 52, BackColor = Color.FromArgb(24, 40, 30), Visible = false };
        wizDoneLbl = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "You're ready — go live!  Run everything from the Song Queue dock in OBS.", ForeColor = Theme.Green, Font = new Font("Segoe UI Semibold", 10.5f), BackColor = Color.Transparent };
        wizDonePanel.Controls.Add(wizDoneLbl); v.Controls.Add(wizDonePanel); y += 66;

        // ---------- activity log + footer ----------
        L(v, X, y, 120, 18, "Activity", cNote, fLbl); y += 20;
        var logPan = new RoundPanel { Left = X, Top = y, Width = W, Height = 92, BackColor = Color.FromArgb(14, 14, 18) };
        txtLog = new TextBox { Left = 12, Top = 10, Width = W - 24, Height = 72, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(14, 14, 18), ForeColor = Color.FromArgb(150, 210, 235), Font = new Font("Consolas", 9f) };
        logPan.Controls.Add(txtLog); v.Controls.Add(logPan); y += 104;

        var footClear = L(v, X, y, 90, 20, "Clear data", cNote, new Font("Segoe UI", 8.5f, FontStyle.Underline)); footClear.Cursor = Cursors.Hand;
        footClear.MouseEnter += (s, e) => footClear.ForeColor = Color.White; footClear.MouseLeave += (s, e) => footClear.ForeColor = cNote;
        footClear.Click += (s, e) => ShowConfirm("Clear saved data?", "This disconnects Spotify and erases your keys, login, request history and logs from this PC.\r\n\r\nThe app stays installed. This can't be undone.", "Clear everything", ClearData);
        var footUn = L(v, X + 100, y, 80, 20, "Uninstall", Color.FromArgb(150, 96, 96), new Font("Segoe UI", 8.5f, FontStyle.Underline)); footUn.Cursor = Cursors.Hand;
        footUn.MouseEnter += (s, e) => footUn.ForeColor = Theme.Bad; footUn.MouseLeave += (s, e) => footUn.ForeColor = Color.FromArgb(150, 96, 96);
        footUn.Click += (s, e) => ShowConfirm("Uninstall Song Requests?", "This removes the app and all of its saved data from this PC.\r\n\r\nIt won't touch your Streamer.bot actions or OBS sources. This can't be undone.", "Uninstall", Uninstall);
        y += 44;
        return y;   // total design height -> the ScrollHost scales + sizes to this
    }

    // ---------------- live status probe (feeds the green dots + detail lines on the setup page) ----------------
    // Each step's dot (wizDot[0..4]) watches the real thing - Streamer.bot's socket, the engine's mutex,
    // token.txt / the YouTube-only choice, the saved reward name, the OBS player heartbeat - and goes green
    // on its own. No "Next" buttons, no state to get wrong.

    // Read one key straight from config.txt (the ENGINE owns some keys the textboxes don't show).
    string ReadCfgValue(string key)
    {
        try
        {
            if (!File.Exists(ConfigFile)) return "";
            foreach (var raw in File.ReadAllLines(ConfigFile))
            {
                var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
                int i = line.IndexOf('='); if (i < 0) continue;
                if (line.Substring(0, i).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line.Substring(i + 1).Trim();
            }
        }
        catch { }
        return "";
    }

    // Merge one key into config.txt without touching anything else (mirror of the engine's SetConfigValue).
    void MergeSaveKey(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
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
        catch { }
    }

    // Fire one round of checks off the UI thread, then apply the results back on it. Never overlaps itself.
    async void ProbeWizard()
    {
        if (_wizProbeBusy || wizDot == null) return;
        _wizProbeBusy = true;
        try
        {
            string wsAddr = txtWs != null ? txtWs.Text.Trim() : "ws://127.0.0.1:8080/";
            bool sb = false, engine = false, spotify = false, playerAlive = false, spConnected = false;
            string reward = "";
            await Task.Run(async () =>
            {
                // 1: Streamer.bot's WebSocket port answers a TCP connect
                try
                {
                    var u = new Uri(wsAddr.Replace("ws://", "http://").Replace("wss://", "https://"));
                    using var tcp = new System.Net.Sockets.TcpClient();
                    var ct = tcp.ConnectAsync(u.Host, u.Port > 0 ? u.Port : 8080);
                    sb = await Task.WhenAny(ct, Task.Delay(350)) == ct && tcp.Connected;
                }
                catch { }
                engine = Engine.IsRunning();                                       // 2: engine mutex
                try { spotify = File.Exists(TokenFile) && File.ReadAllText(TokenFile).Trim().Length > 0; } catch { }   // 3a
                reward = ReadCfgValue("RewardName");                               // 4
                if (engine)
                {
                    // 5: the OBS player heartbeat, via the engine's own /queue state (needs the control token)
                    try
                    {
                        using var cts = new System.Threading.CancellationTokenSource(900);
                        if (string.IsNullOrEmpty(_wizTok))
                        {
                            var tj = await Http.GetStringAsync("http://127.0.0.1:8090/token", cts.Token);
                            var tm = System.Text.RegularExpressions.Regex.Match(tj, "\"tok\"\\s*:\\s*\"([0-9a-f]+)\"");
                            if (tm.Success) _wizTok = tm.Groups[1].Value;
                        }
                        if (!string.IsNullOrEmpty(_wizTok))
                        {
                            var qj = await Http.GetStringAsync("http://127.0.0.1:8090/queue?tok=" + _wizTok, cts.Token);
                            playerAlive = qj.Contains("\"playerAlive\":true");
                            spConnected = qj.Contains("\"spConnected\":true");
                        }
                    }
                    catch { _wizTok = null; }   // engine restarted -> token rotates; re-fetch next round
                }
            });
            if (IsDisposed || wizDot[0].IsDisposed) return;

            void Set(int i, bool ok, string okText, string waitText)
            { wizDot[i].ForeColor = ok ? Theme.Green : Theme.Gray; wizDetail[i].Text = ok ? okText : waitText; wizDetail[i].ForeColor = ok ? Theme.Green : Theme.Gray; }

            Set(0, sb, "connected on " + wsAddr, "open Streamer.bot; WebSocket Server must be ON (address " + wsAddr + ")");
            Set(1, engine, "running - it adds itself to OBS via the script, or use \"start it now\"", "not running - click \"start it now\" (or just open OBS once the script is set up)");
            bool lane = true;   // YouTube is always available, so there's always a usable music source; Spotify is just an extra lane
            Set(2, lane, spotify || spConnected ? "Spotify connected ✓  —  YouTube works too" : "YouTube-only ✓  —  viewers request by song name or YouTube link  (connect Spotify above to add it)", "");
            Set(3, !string.IsNullOrWhiteSpace(reward), "reward: \"" + reward + "\"  - must match the Twitch reward name exactly", "create a channel-point reward, type its name on the Setup tab, click Save");
            Set(4, playerAlive, "OBS player source detected - audio will play through OBS", "add the script once (button on the right), then keep OBS open");

            bool ready = sb && engine && lane && !string.IsNullOrWhiteSpace(reward);
            wizDonePanel.Visible = ready;
            wizDoneLbl.Text = playerAlive
                ? "You're ready - go live!  Run everything from the Song Queue dock in OBS."
                : "Almost there - everything works; open OBS (with the script added) so songs can play.";
        }
        catch { }
        finally { _wizProbeBusy = false; }
    }

    static bool EngineRunning() { return Engine.IsRunning(); }

    void UpdateEngineStatus()
    {
        if (lblEngine == null || btnQueue == null) return;
        bool run = EngineRunning();
        lblEngine.Text = run ? "   ●   Queue: running" : "   ●   Queue: stopped";
        lblEngine.ForeColor = run ? Theme.Green : Theme.Bad;
        enginePill.BackColor = run ? Color.FromArgb(24, 40, 30) : Color.FromArgb(40, 28, 28);
        enginePill.Invalidate();
        btnQueue.Text = run ? "Stop queue" : "Start queue";
        btnQueue.Base = run ? Theme.GrayBtn : Theme.Green;
        btnQueue.Hover = run ? Theme.GrayBtnHi : Theme.GreenHi;
        btnQueue.G2 = run ? (Color?)null : Theme.GreenDark;   // solid gray when running, green gradient when stopped
        btnQueue.Invalidate();
    }

    void ToggleEngine() { if (EngineRunning()) StopEngine(); else StartEngine(); UpdateEngineStatus(); }

    void StartEngine()
    {
        try
        {
            // Spotify is OPTIONAL (YouTube-only works) - the engine just needs a saved config to boot.
            if (!File.Exists(ConfigFile)) { SaveConfig(); Log("Saved your settings first."); }
            string exe = Environment.ProcessPath;   // <install>\app\SongRequests.exe - run the engine directly
            var psi = new ProcessStartInfo(exe, "--engine") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) };
            psi.Environment["SQ_ROOT"] = InstallRoot();
            Process.Start(psi);
            bool spot = false; try { spot = File.Exists(TokenFile) && File.ReadAllText(TokenFile).Trim().Length > 0; } catch { }
            Log(spot ? "Queue started - redemptions will now be added to Spotify. (Keep Spotify open + playing.)"
                     : "Queue started (YouTube-only - connect Spotify on the Setup tab to add a Spotify lane).");
        }
        catch (Exception e) { Log("Couldn't start the queue: " + e.Message); }
    }

    void StopEngine()
    {
        try { Engine.SignalStop(); Log("Queue stopped."); }
        catch (Exception e) { Log("Couldn't stop the queue: " + e.Message); }
    }

    // ---- run-at-Windows-startup toggle (HKCU Run key) ----
    static bool StartupEnabled()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunVal) != null; } catch { return false; }
    }
    void SetStartup(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunVal, "\"" + Path.Combine(InstallRoot(), "SongRequests.exe") + "\" --engine");   // the root launcher
            else k.DeleteValue(RunVal, false);
        }
        catch (Exception e) { Log("Startup setting failed: " + e.Message); }
    }
    void BuildInfoOverlay()
    {
        var pbg = Color.FromArgb(30, 30, 40);
        infoPanel = new RoundPanel { Left = 20, Top = 66, Width = Width - 40, Height = Height - 66 - 20, BackColor = pbg, Visible = false };
        infoTitle = new Label { Left = 24, Top = 20, AutoSize = true, ForeColor = Theme.Purple, Font = new Font("Segoe UI Semibold", 13f), BackColor = pbg };
        var cls = new Label { Text = "✕", Width = 30, Height = 28, Top = 18, Left = infoPanel.Width - 46, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Gray, Font = new Font("Segoe UI", 11f), Cursor = Cursors.Hand, BackColor = pbg };
        cls.MouseEnter += (s, e) => cls.ForeColor = Color.White;
        cls.MouseLeave += (s, e) => cls.ForeColor = Theme.Gray;
        cls.Click += (s, e) => infoPanel.Visible = false;
        infoBody = new Label { Left = 24, Top = 62, Width = infoPanel.Width - 48, Height = infoPanel.Height - 82, ForeColor = Color.FromArgb(220, 220, 232), Font = new Font("Segoe UI", 10.75f), BackColor = pbg };
        infoPanel.Controls.Add(infoTitle); infoPanel.Controls.Add(cls); infoPanel.Controls.Add(infoBody);
        Controls.Add(infoPanel);
    }

    void ShowInfo(string title, string body) { infoTitle.Text = title; infoBody.Text = body; infoPanel.Visible = true; infoPanel.BringToFront(); }
    static void OpenUrl(string u) { try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } }

    bool FocusStreamerBot()
    {
        try
        {
            Process target = null;
            foreach (var p in Process.GetProcessesByName("Streamer.bot"))
                if (p.MainWindowHandle != IntPtr.Zero) { target = p; break; }
            if (target == null)
                foreach (var p in Process.GetProcesses())
                    try { if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.IndexOf("Streamer.bot", StringComparison.OrdinalIgnoreCase) >= 0) { target = p; break; } } catch { }
            if (target == null) return false;
            var h = target.MainWindowHandle;
            if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
            SetForegroundWindow(h);
            return true;
        }
        catch { return false; }
    }

    string ObsHelp()
    {
        return
            "Shows the current song (with album art + what's up next) on stream.\r\n" +
            "No extra setup, ports or credentials - it just works with the engine.\r\n\r\n" +
            "Your overlay file  (path COPIED to your clipboard):\r\n" +
            "    " + OverlayFile + "\r\n\r\n" +
            "OPTION 1  -  Drag & drop  (easiest)\r\n" +
            "    Open that folder, then drag  nowplaying.html  straight into your OBS scene.\r\n" +
            "    Click it, and set the size to  640 x 150.\r\n\r\n" +
            "OPTION 2  -  Add it manually\r\n" +
            "    Sources  ->  +  ->  Browser  ->  name it \"Now Playing\"  ->  OK\r\n" +
            "    Tick \"Local file\", Browse to the file above (or paste the path),\r\n" +
            "    set  Width 640,  Height 150,  then OK.\r\n\r\n" +
            "Either way, drag the box exactly where you want it. It slides in while a\r\n" +
            "song plays and fades out when nothing is. Needs the engine running - same as the queue.";
    }

    // The overlay ships as nowplaying-default.html (NOT nowplaying.html), so an app update never overwrites a user's
    // customized nowplaying.html. Materialize the working copy from the default on first run if it's missing.
    void EnsureOverlay()
    {
        try
        {
            string root = InstallRoot();
            string live = Path.Combine(root, "nowplaying.html"), def = Path.Combine(root, "nowplaying-default.html");
            if (!File.Exists(live) && File.Exists(def)) File.Copy(def, live);
        }
        catch { }
    }

    // Point the streamer at the GitHub guide for building their own Now-Playing overlay + where to save it.
    void ShowCustomOverlay()
    {
        EnsureOverlay();
        OpenUrl("https://github.com/Goku67HoodWars/Streamer.bot-Song-Requests/blob/main/docs/custom-overlay.md");
        try { Clipboard.SetText(OverlayFile); } catch { }
        ShowInfo("Design your own Now-Playing overlay",
            "Opened the step-by-step guide on GitHub in your browser. It gives you:\r\n\r\n" +
            "   •  the live song data the app serves (title, artist, album art, progress, what's next)\r\n" +
            "   •  a ready-to-paste prompt so an AI can build the HTML overlay for you\r\n" +
            "   •  exactly where to save the finished file\r\n\r\n" +
            "Save your file here, REPLACING the built-in overlay  (path COPIED to your clipboard):\r\n" +
            "    " + OverlayFile + "\r\n\r\n" +
            "It takes over the “Now Playing” Browser source you already have in OBS - no re-adding.\r\n" +
            "Updates will NOT overwrite it. (Prefer a separate source? Save the file anywhere and add a new\r\n" +
            "OBS Browser source with “Local file” pointing at it.)");
    }

    // One-time OBS setup that auto-starts/stops the engine with OBS (replaces the old Windows-startup
    // toggle + Streamer.bot Start/Stop import - a browser dock can't launch a process, so OBS does it).
    void ShowObsAutostart()
    {
        string lua = Path.Combine(InstallRoot(), "obs-autostart.lua");
        try { Clipboard.SetText(lua); } catch { }
        ShowInfo("Set up OBS  (one script does it all)",
            "This one script runs the engine whenever OBS is open AND creates the\r\n" +
            "\"YouTube Player\" audio source for you, wired to play through OBS - no\r\n" +
            "manual source, no audio settings to get wrong.\r\n\r\n" +
            "Your script file  (path COPIED to your clipboard):\r\n" +
            "    " + lua + "\r\n\r\n" +
            "SET UP ONCE - in OBS:\r\n" +
            "     STEP 1  -  Tools  ->  Scripts\r\n" +
            "     STEP 2  -  Click the  +  button (bottom-left)\r\n" +
            "     STEP 3  -  Click the \"File name\" box, paste the path above  (Ctrl+V)\r\n" +
            "     STEP 4  -  Click Open.  Done.\r\n\r\n" +
            "From now on the engine starts with OBS (and stops when OBS closes), and the\r\n" +
            "audio source is always there. Add the control dock with the \"Add the queue\r\n" +
            "dock\" button. Streamer.bot still has to be open for redemptions.");
    }

    // A ready-made Streamer.bot import for the "Song Requests Announce" action: one Twitch Send-Message
    // sub-action (type 10) whose text is %message%. Built from a real SB export (format version 23), so it
    // imports cleanly with no manual steps. The engine fires it via DoAction with a "message" argument.
    const string AnnounceImportCode = "U0JBRR+LCACvDUhqAv+FUslu2zAQ/RWBQG6WIcl2vNzcAl1uQVL0UuQwIscSES4ql9iG4X/v0FqQpgGqgyS+Gb6Z92YuTGMAtrswAxrZjj1Z02SP+DuiDz7bG2Oj4chmDGJorXufQYFXdF5aQ5FyXswLQgR67mQXevTBJiYE3mY+XXX9Vfr6qEIWbPbjKANFeQth3leyj9HseU9golIzpqWROuqfY7GEXqkU9N3DLdmz3a8Lk4KqruoStmKNecGXy3yJ5SbfQlXma14cFliJeyhTq9RKTLKL4ck/eI0PpaOBWiHxBxeRjieuosAvzupv0gfrzmx3AOX/Dj2gEdI0U+h/TjfOxu4Do0Ed4ezJmYnJgRFWj0YNILeGR+fQhAkKTjYNOUf2PM+Yj/X+jV0BT5TJ7jR6Dw3eUaXo8ZMNo0oiUTXwl/F883d7qPimXkBeitWK/C3W+aYmpyu4xxUvD1uxWBDTEWXTEhMtBrVx7kh4SX8dpP6+i3G673yVRuCJLl2fkxyloPMoviZbbhISfBvcIIhbrcmJ4XTE2lv+guEJ3eskekI/K0mlBzRIPWRc3yxytUjz66wLKNIAh9Vesn/XsN/5HFTXwrxk1z/kRckSUAMAAA==";

    void ShowChatSetup()
    {
        try { Clipboard.SetText(AnnounceImportCode); } catch { }
        bool focused = FocusStreamerBot();
        ShowInfo("Chat replies  -  import into Streamer.bot",
            (focused ? "COPIED!  Streamer.bot is now in front.\r\n\r\n"
                     : "COPIED to your clipboard.\r\n(Open Streamer.bot first if it isn't running.)\r\n\r\n") +
            "The \"Song Requests Announce\" action is on your clipboard. In Streamer.bot:\r\n" +
            "     STEP 1  -  Actions tab  ->  \"Import\" button in the top toolbar\r\n" +
            "     STEP 2  -  Click the big box and paste  (Ctrl+V)\r\n" +
            "     STEP 3  -  Click \"Import\".  Done - the action is ready.\r\n\r\n" +
            "Then turn on \"Announce queued songs in chat\" in the OBS dock, and it posts\r\n" +
            "     @viewer  ✅ queued: Song - Artist   for each request.\r\n\r\n" +
            "Prefer to make it by hand?  New action named EXACTLY  Song Requests Announce,\r\n" +
            "add sub-action  Twitch -> Chat -> Send Message to Channel,  message =  %message%\r\n" +
            "(Keep Streamer.bot open, as always.)");
    }

    Label WinBtn(string t, int left)
    {
        var l = new Label { Text = t, Left = left, Top = 15, Width = 30, Height = 28, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Gray, Font = new Font("Segoe UI", 11f), Cursor = Cursors.Hand };
        l.MouseEnter += (s, e) => { if (l.ForeColor != Color.FromArgb(240, 90, 90)) l.ForeColor = Color.White; };
        l.MouseLeave += (s, e) => l.ForeColor = Theme.Gray;
        return l;
    }

    // ---------------- config ----------------
    void LoadConfig()
    {
        var c = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(ConfigFile))
                foreach (var raw in File.ReadAllLines(ConfigFile))
                {
                    var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
                    int i = line.IndexOf('='); if (i < 0) continue;
                    c[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
        }
        catch { }
        txtId.Text     = Cfg(c, "SpotifyClientId");
        txtSecret.Text = Cfg(c, "SpotifyClientSecret");
        txtReward.Text = Cfg(c, "RewardName", "Song Request");
        txtWs.Text     = Cfg(c, "StreamerBotWs", "ws://127.0.0.1:8080/");
    }

    void SaveConfig()
    {
        Directory.CreateDirectory(DataDir);
        // Merge-save: update the keys the UI owns, keep everything else (hand-edited / engine-only keys).
        var c = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(ConfigFile))
                foreach (var raw in File.ReadAllLines(ConfigFile))
                {
                    var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
                    int i = line.IndexOf('='); if (i < 0) continue;
                    c[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
        }
        catch { }
        c["SpotifyClientId"] = txtId.Text.Trim();
        c["SpotifyClientSecret"] = txtSecret.Text.Trim();
        c["RewardName"] = txtReward.Text.Trim();
        c["StreamerBotWs"] = txtWs.Text.Trim();
        c["RedirectPort"] = RedirectPort.ToString();
        // YouTubeEnabled + MaxSongSeconds are owned by the dock/engine now - merge-save leaves whatever's in the file untouched.
        var sb = new StringBuilder();
        foreach (var kv in c) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
        File.WriteAllText(ConfigFile, sb.ToString());
    }

    // Opt-in: add the "Song Queue" control dock to OBS (one-time write to OBS's config: user.ini on OBS 30.2+, else global.ini).
    // OBS rewrites that file when it closes, so this only sticks while OBS is closed - we guide accordingly.
    const string DockUrl = "http://127.0.0.1:8090/dock";
    void AddQueueDockClicked()
    {
        try { Clipboard.SetText(DockUrl); } catch { }
        string manual = "Prefer to do it yourself?  OBS -> View -> Docks -> Custom Browser Docks...\r\n" +
                        "Name: Song Queue    URL (copied): " + DockUrl;
        if (!ObsConfig.ObsInstalled)
        { ShowInfo("Add the queue dock", "Couldn't find OBS on this PC. Install OBS and try again, or add it by hand:\r\n\r\n" + manual); return; }
        if (ObsConfig.DockPresent(DockUrl))
        { ShowInfo("Already added ✓", "The \"Song Queue\" dock is already in OBS. Open OBS and drag it wherever you like.\r\n\r\nDon't want it? Right-click the dock -> Close (it won't come back)."); return; }
        if (ObsConfig.IsObsRunning())
        { ShowInfo("Close OBS first", "Please CLOSE OBS, then click this again.\r\n\r\nOBS overwrites its layout file when it closes, so it would undo the change while it's open.\r\n\r\n" + manual); return; }
        if (ObsConfig.AddQueueDock("Song Queue", DockUrl))
            ShowInfo("Dock added ✓", "The \"Song Queue\" dock appears the next time you open OBS - drag it anywhere in your layout. It's your control panel: requests on/off, volume, skip, and the live queue.\r\n\r\nAdded once - it won't keep coming back. Don't want it later? Right-click the dock -> Close.");
        else
            ShowInfo("Add the queue dock", "Couldn't write to OBS's config automatically. Add it by hand:\r\n\r\n" + manual);
    }

    // One-click "make YouTube audible in OBS": OBS browser sources block autoplay-with-sound unless
    // OBS is launched with --autoplay-policy=no-user-gesture-required. We add that flag to the OBS
    // shortcut(s) the user actually launches (no admin needed for their own Desktop/Start shortcuts),
    // and if none are writable we drop a ready-to-use flagged launcher on the Desktop.
    void FixObsAutoplay()
    {
        string msg = ObsFixReport(out int patched, out bool needAdmin);
        Log("OBS sound fix: patched " + patched + " shortcut(s)." + (needAdmin ? " (system shortcut needs admin)" : ""));
        if (needAdmin)
        {
            // Windows Search / Start-menu / taskbar launches use the all-users shortcut, which needs
            // admin to edit. Offer to do it with one UAC prompt.
            string lead = patched > 0
                ? "Patched your own OBS shortcut(s). But if you open OBS from "
                : "You appear to open OBS from ";
            var r = MessageBox.Show(
                lead + "Windows Search or the Start menu, that uses a system shortcut which needs admin to edit.\r\n\r\n" +
                "Fix that one now? Windows will show a permission prompt.",
                "Fix YouTube sound in OBS", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--fix-obs-admin") { UseShellExecute = true, Verb = "runas" });
                    try { p.WaitForExit(15000); } catch { }
                    ShowInfo("Fix YouTube sound in OBS", "Done. Your OBS shortcut (the one Windows Search / the Start menu uses) now has the flag.\r\n\r\nNow fully close OBS (tray icon -> Exit) and reopen it - YouTube requests will play with sound.");
                }
                catch { ShowInfo("Fix YouTube sound in OBS", "The permission prompt was declined, so the system shortcut wasn't changed.\r\n\r\nClick the button again to retry, or launch OBS from the Desktop shortcut (already fixed)."); }
                return;
            }
        }
        ShowInfo("Fix YouTube sound in OBS", msg);
    }

    const string ObsFlag = "--autoplay-policy=no-user-gesture-required";

    // Patches one .lnk if it targets obs64.exe. Returns 0 not-obs / 1 patched / 2 already-flagged / 3 write-denied.
    static int PatchObsLnk(Type t, object shell, string lnkPath, ref string obsExe)
    {
        try
        {
            object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            try
            {
                Type st = sc.GetType();
                string tgt = st.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
                if (!tgt.EndsWith("obs64.exe", StringComparison.OrdinalIgnoreCase)) return 0;
                if (string.IsNullOrEmpty(obsExe)) obsExe = tgt;
                string args = st.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
                if (args.IndexOf(ObsFlag, StringComparison.OrdinalIgnoreCase) >= 0) return 2;
                st.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { (args.Trim() + " " + ObsFlag).Trim() });
                try { st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null); return 1; }
                catch { return 3; }
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(sc); } catch { } }
        }
        catch { return 0; }
    }

    static void ScanObsDir(Type t, object shell, string dir, List<string> patched, ref string obsExe, ref bool already, ref bool needAdmin)
    {
        if (!Directory.Exists(dir)) return;
        string[] lnks; try { lnks = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); } catch { return; }
        foreach (var lnk in lnks)
        {
            if (Path.GetFileName(lnk).IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            int r = PatchObsLnk(t, shell, lnk, ref obsExe);
            if (r == 1) patched.Add(Path.GetFileName(lnk)); else if (r == 2) already = true; else if (r == 3) needAdmin = true;
        }
    }

    // Core (no UI): patches the user's own OBS shortcuts; reports the outcome and whether an
    // admin-only (all-users) OBS shortcut still needs the flag.
    public static string ObsFixReport(out int patchedCount, out bool needAdmin)
    {
        patchedCount = 0; needAdmin = false;
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return "Couldn't access Windows shortcuts on this PC.\r\n\r\nAdd this to your OBS shortcut's Target by hand instead:\r\n   " + ObsFlag;
            object shell = Activator.CreateInstance(t);
            var patched = new List<string>(); string obsExe = FindObsExe(); bool already = false;
            try
            {
                ScanObsDir(t, shell, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), patched, ref obsExe, ref already, ref needAdmin);
                ScanObsDir(t, shell, Environment.GetFolderPath(Environment.SpecialFolder.Programs), patched, ref obsExe, ref already, ref needAdmin);   // user Start menu
                ScanObsDir(t, shell, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), patched, ref obsExe, ref already, ref needAdmin);
                ScanObsDir(t, shell, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"), patched, ref obsExe, ref already, ref needAdmin);  // all-users (Search/Start)

                // Nothing writable, nothing already flagged, and no admin path to offer -> drop a Desktop launcher.
                if (patched.Count == 0 && !already && !needAdmin && !string.IsNullOrEmpty(obsExe) && File.Exists(obsExe))
                    try
                    {
                        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OBS (Song Requests).lnk");
                        object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                        Type st = sc.GetType();
                        st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { obsExe });
                        st.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { ObsFlag });
                        st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(obsExe) });
                        st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                        patched.Add("OBS (Song Requests).lnk  [new Desktop shortcut]");
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(sc);
                    }
                    catch { }
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { } }

            patchedCount = patched.Count;
            if (patched.Count > 0)
                return "Added the YouTube-sound flag to your OBS shortcut(s):\r\n\r\n   " + string.Join("\r\n   ", patched) +
                       "\r\n\r\nFully close OBS (tray icon -> Exit), reopen it from that shortcut, and YouTube will have sound.";
            if (already && !needAdmin)
                return "Your OBS shortcut already has the YouTube-sound flag. 👍\r\n\r\nJust fully close OBS (tray icon -> Exit) and reopen it.";
            if (needAdmin)
                return "To finish, add this to your OBS shortcut's Target by hand (the Start-menu one needs admin), then restart OBS:\r\n   " + ObsFlag;
            if (!string.IsNullOrEmpty(obsExe))
                return "Found OBS, but couldn't edit a shortcut.\r\n\r\nAdd this to your OBS shortcut Target by hand, then restart OBS:\r\n   " + ObsFlag;
            return "Couldn't find OBS on this PC. Install/open OBS once, then click this again - or add this to your OBS shortcut Target:\r\n   " + ObsFlag;
        }
        catch (Exception e) { return "Couldn't apply the fix automatically: " + e.Message + "\r\n\r\nAdd this to your OBS shortcut Target by hand:\r\n   " + ObsFlag; }
    }

    // Elevated entry (--fix-obs-admin): patch ONLY the all-users OBS shortcut(s). We deliberately avoid
    // per-user folders here because when elevated they can resolve to a different account.
    public static void PatchSystemObs()
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell"); if (t == null) return;
            object shell = Activator.CreateInstance(t); string obsExe = null;
            try
            {
                string sysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
                if (Directory.Exists(sysDir))
                    foreach (var lnk in Directory.GetFiles(sysDir, "*.lnk", SearchOption.AllDirectories))
                        if (Path.GetFileName(lnk).IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) < 0)
                            PatchObsLnk(t, shell, lnk, ref obsExe);
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { } }
        }
        catch { }
    }

    static string FindObsExe()
    {
        try { foreach (var p in Process.GetProcessesByName("obs64")) { try { string f = p.MainModule?.FileName; if (!string.IsNullOrEmpty(f)) return f; } catch { } } } catch { }
        foreach (var c in new[] { @"C:\Program Files\obs-studio\bin\64bit\obs64.exe", @"C:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe" })
            try { if (File.Exists(c)) return c; } catch { }
        return null;
    }

    static string Cfg(Dictionary<string, string> c, string k, string def = "")
        => c.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

    async Task ConnectSpotify()
    {
        ClientId = txtId.Text.Trim(); ClientSecret = txtSecret.Text.Trim();
        RedirectUri = $"http://127.0.0.1:{RedirectPort}/callback";
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        { MessageBox.Show("Enter your Spotify Client ID and Secret first."); return; }
        SaveConfig();
        try
        {
            btnConnect.Enabled = false; btnConnect.Text = "Connecting...";
            await FirstTimeAuth();
            SetSpotify(true, "connected");
            Log("Spotify connected. You're all set!");
            ProbeWizard();   // reflect the new connection in the step dots
        }
        catch (Exception e) { Log("Connect failed: " + e.Message); SetSpotify(false, "failed"); }
        finally { btnConnect.Enabled = true; btnConnect.Text = "Connect Spotify"; }
    }

    async Task FirstTimeAuth()
    {
        string scope = "user-modify-playback-state user-read-playback-state";
        string url = "https://accounts.spotify.com/authorize?client_id=" + Uri.EscapeDataString(ClientId) +
                     "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                     "&scope=" + Uri.EscapeDataString(scope);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{RedirectPort}/");
        listener.Start();
        Log("Opening browser for Spotify login...");
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { Log("Open this URL:\r\n" + url); }
        // Wait for the real OAuth callback - ignore favicon/probe requests, and time out so the button never sticks.
        HttpListenerContext ctx = null;
        string code = null, err = null;
        var deadline = Task.Delay(TimeSpan.FromMinutes(3));
        while (true)
        {
            var ctxTask = listener.GetContextAsync();
            if (await Task.WhenAny(ctxTask, deadline) == deadline)
            { listener.Stop(); try { await ctxTask; } catch { } throw new Exception("Login timed out - click Connect Spotify again to retry."); }
            ctx = await ctxTask;
            code = ctx.Request.QueryString["code"]; err = ctx.Request.QueryString["error"];
            if (code != null || err != null) break;                              // the real Spotify callback
            try { ctx.Response.StatusCode = 404; ctx.Response.Close(); } catch { }  // ignore favicon / stray probes
        }
        byte[] b = Encoding.UTF8.GetBytes("<html><body style='font-family:sans-serif;background:#0f0f14;color:#fff;text-align:center;padding-top:70px'><h2>" +
            (code != null ? "Connected! You can close this tab." : "Auth failed: " + err) + "</h2></body></html>");
        ctx.Response.ContentType = "text/html"; ctx.Response.ContentLength64 = b.Length;
        await ctx.Response.OutputStream.WriteAsync(b, 0, b.Length); ctx.Response.Close(); listener.Stop();
        if (code == null) throw new Exception(err ?? "no code");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + ClientSecret)));
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { {"grant_type","authorization_code"}, {"code",code}, {"redirect_uri",RedirectUri} });
        using var resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception((int)resp.StatusCode + ": " + body);
        File.WriteAllText(TokenFile, JsonDocument.Parse(body).RootElement.GetProperty("refresh_token").GetString());
    }

    void SetSpotify(bool ok, string s)
    {
        lblSpotify.Text = "   ●   Spotify: " + s;
        lblSpotify.ForeColor = ok ? Theme.Green : Theme.Bad;
        statusPill.BackColor = ok ? Color.FromArgb(24, 40, 30) : Color.FromArgb(40, 28, 28);
        statusPill.Invalidate();
    }

    // ---------------- confirm overlay + maintenance ----------------
    void BuildConfirmOverlay()
    {
        var pbg = Color.FromArgb(30, 30, 40);
        confirmPanel = new RoundPanel { Left = 46, Top = 258, Width = Width - 92, Height = 258, BackColor = pbg, Visible = false };
        confirmTitle = new Label { Left = 26, Top = 24, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 13.5f), BackColor = pbg };
        confirmBody = new Label { Left = 26, Top = 64, Width = confirmPanel.Width - 52, Height = 120, ForeColor = Color.FromArgb(220, 220, 232), Font = new Font("Segoe UI", 10.5f), BackColor = pbg };
        confirmNo = new RoundButton { Text = "Cancel", Left = 26, Top = confirmPanel.Height - 62, Width = 150, Height = 46, Base = Theme.GrayBtn, Hover = Theme.GrayBtnHi, Font = new Font("Segoe UI Semibold", 10.5f) };
        confirmYes = new RoundButton { Text = "Confirm", Left = confirmPanel.Width - 26 - 210, Top = confirmPanel.Height - 62, Width = 210, Height = 46, Base = Theme.Bad, Hover = Color.FromArgb(242, 110, 110), Font = new Font("Segoe UI Semibold", 10.5f) };
        confirmNo.Click += (s, e) => { confirmPanel.Visible = false; pendingConfirm = null; };
        confirmYes.Click += (s, e) => { confirmPanel.Visible = false; var a = pendingConfirm; pendingConfirm = null; if (a != null) a(); };
        confirmPanel.Controls.AddRange(new Control[] { confirmTitle, confirmBody, confirmNo, confirmYes });
        Controls.Add(confirmPanel);
    }

    void ShowConfirm(string title, string body, string yesText, Action action)
    {
        confirmTitle.Text = title; confirmBody.Text = body; confirmYes.Text = yesText;
        pendingConfirm = action;
        confirmPanel.Visible = true; confirmPanel.BringToFront();
    }

    void ClearData()
    {
        try
        {
            Engine.SignalStop();   // stop the queue engine so it isn't rewriting the files we're wiping
            SetStartup(false);     // don't leave a login task that launches an unconfigured engine
            foreach (var n in new[] { "config.txt", "token.txt", "log.txt", "requests.json", "yt-dlp.exe", "resume_owed.txt" })
                try { File.Delete(Path.Combine(DataDir, n)); } catch { }
            txtId.Text = ""; txtSecret.Text = ""; txtReward.Text = "Song Request"; txtWs.Text = "ws://127.0.0.1:8080/";
            SetSpotify(false, "not connected");
            txtLog.Clear();
            Log("Saved data cleared - Spotify disconnected, request log wiped.");
            ProbeWizard();
        }
        catch (Exception e) { Log("Clear failed: " + e.Message); }
    }

    static readonly string StartMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Song Requests.lnk");
    static readonly string DesktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Song Requests.lnk");

    // Builds + launches the self-deleting uninstall batch (also removes the Start-menu shortcut + Add/Remove Programs entry).
    // Returns false only if the install folder fails the safety check. Shared by the in-app link and the --uninstall entry.
    static bool RunUninstall()
    {
        string installDir = InstallRoot();   // the whole install (root launcher + app\ + assets)
        string dataDir = DataDir.TrimEnd('\\');
        bool safeInstall = !string.IsNullOrWhiteSpace(installDir) && installDir.Length > 8
            && Directory.Exists(installDir) && Directory.GetParent(installDir) != null
            && File.Exists(Path.Combine(installDir, "SongRequests.exe"))
            && File.Exists(Path.Combine(installDir, "app", "SongRequests.exe"));
        bool safeData = dataDir.EndsWith("SongRequests", StringComparison.OrdinalIgnoreCase) && Directory.GetParent(dataDir) != null;
        if (!safeInstall) return false;

        Engine.SignalStop();   // ask the queue engine (SongRequests.exe --engine) to exit so the wait loop below can complete

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("cd /d \"%TEMP%\"");
        sb.AppendLine("set /a sqtries=0");
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq SongRequests.exe\" 2>nul | find /I \"SongRequests.exe\" >nul || goto ready");
        sb.AppendLine("set /a sqtries+=1");
        sb.AppendLine("if %sqtries% geq 30 goto ready");   // bounded: never wait forever if a process lingers
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("goto wait");
        sb.AppendLine(":ready");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("del /f /q \"" + StartMenuLnk + "\" 2>nul");
        sb.AppendLine("del /f /q \"" + DesktopLnk + "\" 2>nul");   // remove the optional desktop shortcut too
        sb.AppendLine("reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v SongRequests /f 2>nul");
        sb.AppendLine("reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SongRequests\" /f 2>nul");
        sb.AppendLine("rmdir /s /q \"" + installDir + "\"");
        if (safeData) sb.AppendLine("rmdir /s /q \"" + dataDir + "\"");
        sb.AppendLine("del /f /q \"%~f0\"");
        string bat = Path.Combine(Path.GetTempPath(), "sq_uninstall.bat");
        File.WriteAllText(bat, sb.ToString());
        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        return true;
    }

    void Uninstall()
    {
        try { if (RunUninstall()) Application.Exit(); else Log("Uninstall stopped: install folder looks unexpected."); }
        catch (Exception e) { Log("Uninstall failed: " + e.Message); }
    }

    public static void UninstallHeadless()
    {
        try
        {
            if (MessageBox.Show("Remove Song Requests and all its saved data?\r\n\r\n(This won't touch your Streamer.bot actions or OBS sources.)",
                "Uninstall Song Requests", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunUninstall();
        }
        catch { }
    }

    // Inline update check (no popup) - results appear in the sidebar footer.
    // No "check for updates" button anymore. On every open we ask the release CDN (never the rate-limited
    // API) what the latest version is and, if we're behind, install it right then. The launcher already
    // applies app-only updates before we launch, so in practice this only fires for a runtime hop (which the
    // launcher can't self-apply). Silent + best-effort: offline or up-to-date does nothing; a dev build
    // (version ahead of the release) never self-downgrades.
    async void MaybeAutoUpdate()
    {
        try
        {
            string dir = AppContext.BaseDirectory.TrimEnd('\\');
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.Add("User-Agent", "SongRequests");
            var m = JsonDocument.Parse(await h.GetStringAsync(RepoDl + "manifest.json")).RootElement;
            string tag = m.TryGetProperty("app", out var av) ? av.GetString() : null;
            if (string.IsNullOrEmpty(tag) || !RemoteNewerTag(AppVersion, tag)) return;   // up to date / ahead
            string wantRt = m.TryGetProperty("runtime", out var rv) ? (rv.GetString() ?? "1") : "1";
            string rtFile = Path.Combine(dir, "runtime.txt");
            string localRt = File.Exists(rtFile) ? File.ReadAllText(rtFile).Trim() : "";
            bool needRuntime = localRt != wantRt;
            ShowInfo("Updating", "A new version (" + tag + ") is available and is installing now.\r\n\r\nThe app will restart in a moment — this only takes a few seconds.");
            await DoUpdate(RepoDl + "app.zip", needRuntime ? RepoDl + "runtime.zip" : null, wantRt);
        }
        catch { }   // offline / CDN blip -> just run what's installed; we'll catch up next open
    }

    // Strictly-newer version compare so a dev build (ahead of the published release) never self-downgrades.
    static bool RemoteNewerTag(string local, string remote)
    {
        int[] a = ParseVerTag(local), b = ParseVerTag(remote);
        if (a == null || b == null) return false;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        { int x = i < a.Length ? a[i] : 0, yv = i < b.Length ? b[i] : 0; if (yv != x) return yv > x; }
        return false;
    }
    static int[] ParseVerTag(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.TrimStart('v', 'V').Trim(); var parts = v.Split('.'); var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) { string p = parts[i]; int d = p.IndexOf('-'); if (d >= 0) p = p.Substring(0, d); if (!int.TryParse(p, out nums[i])) return null; }
        return nums;
    }

    async Task DoUpdate(string appUrl, string runtimeUrl, string wantRt)
    {
        try
        {
            bool engineWasRunning = Engine.IsRunning();   // so we can bring the queue back after the update
            Engine.SignalStop();   // the update batch waits for every SongRequests.exe to close - the engine is one of them
            string dir = InstallRoot();   // extract to the install ROOT; the zips carry both the root launcher and the app\ tree
            string dq = dir.Replace("'", "''");   // escape for the single-quoted PowerShell -DestinationPath
            string tmp = Path.GetTempPath();
            using (var h = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
            {
                h.DefaultRequestHeaders.Add("User-Agent", "SongRequests");
                if (runtimeUrl != null) { Log("Downloading runtime update..."); File.WriteAllBytes(Path.Combine(tmp, "sq_runtime.zip"), await h.GetByteArrayAsync(runtimeUrl)); }
                Log("Downloading update...");
                File.WriteAllBytes(Path.Combine(tmp, "sq_app.zip"), await h.GetByteArrayAsync(appUrl));
            }
            // helper batch waits for this window to close, extracts the zip(s) in place, relaunches, self-deletes.
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("cd /d \"%TEMP%\"");
            sb.AppendLine("set /a sqtries=0");
            sb.AppendLine(":wait");
            sb.AppendLine("tasklist /FI \"IMAGENAME eq SongRequests.exe\" 2>nul | find /I \"SongRequests.exe\" >nul || goto ready");
            sb.AppendLine("set /a sqtries+=1");
            sb.AppendLine("if %sqtries% geq 30 goto ready");   // bounded: never wait forever if a process lingers
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("goto wait");
            sb.AppendLine(":ready");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            if (runtimeUrl != null)
            {
                sb.AppendLine("powershell -NoProfile -Command \"Expand-Archive -LiteralPath '%TEMP%\\sq_runtime.zip' -DestinationPath '" + dq + "' -Force\"");
                sb.AppendLine("> \"" + Path.Combine(dir, "app", "runtime.txt") + "\" echo " + wantRt);
            }
            sb.AppendLine("powershell -NoProfile -Command \"Expand-Archive -LiteralPath '%TEMP%\\sq_app.zip' -DestinationPath '" + dq + "' -Force\"");
            sb.AppendLine("del /f /q \"%TEMP%\\sq_app.zip\" \"%TEMP%\\sq_runtime.zip\" 2>nul");
            sb.AppendLine("start \"\" \"" + Path.Combine(dir, "SongRequests.exe") + "\"");
            if (engineWasRunning) sb.AppendLine("start \"\" \"" + Path.Combine(dir, "SongRequests.exe") + "\" --engine");   // bring the queue back so the overlay/dock reconnect
            sb.AppendLine("del /f /q \"%~f0\"");
            string bat = Path.Combine(tmp, "sq_update.bat");
            File.WriteAllText(bat, sb.ToString());
            Log("Installing update + restarting...");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = tmp });
            Application.Exit();
        }
        catch (Exception e) { Log("Update failed: " + e.Message); }
    }

    void Log(string m) { txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + m + "\r\n"); }
}

class Req
{
    public string time { get; set; }
    public string user { get; set; }
    public string input { get; set; }
    public string track { get; set; }
    public string uri { get; set; }
    public string status { get; set; }
    public string played { get; set; }
}
