using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace TheaterDim;

static class Program
{
    static Mutex? _single;

    [STAThread]
    static void Main()
    {
        // Single instance: logon task must not spawn a 2nd tray icon / port clash.
        _single = new Mutex(true, "TheaterDim_SingleInstance_2f7a13", out bool created);
        if (!created) return;

        // PerMonitorV2 = correct Screen.Bounds on mixed-DPI multi-monitor setups.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TheaterContext());

        GC.KeepAlive(_single);
    }
}

// Persisted config in %APPDATA%\TheaterDim\settings.json
class Settings
{
    public bool Enabled { get; set; } = true;
    public int DimPercent { get; set; } = 70;       // overlay opacity
    public bool AutoFollow { get; set; } = true;     // true = dim monitors PotPlayer is NOT on
    public string MainDeviceName { get; set; } = ""; // Screen.DeviceName, used when AutoFollow=false
    public string TriggerProcess { get; set; } = "potplayer"; // substring, case-insensitive

    // Web remote
    public bool RemoteEnabled { get; set; } = true;
    public int Port { get; set; } = 8777;
    public string Token { get; set; } = "";          // generated on first run

    public string EnsureToken()
    {
        if (string.IsNullOrEmpty(Token))
        {
            Token = Guid.NewGuid().ToString("N").Substring(0, 16);
            Save();
        }
        return Token;
    }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheaterDim", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt/missing -> defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}

// Black click-through topmost overlay, one per monitor.
class OverlayForm : Form
{
    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Enabled = false; // never accept input
    }

    // Show without stealing focus -> keeps PotPlayer fullscreen alive.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED     = 0x00080000; // alpha
            const int WS_EX_TRANSPARENT = 0x00000020; // click-through
            const int WS_EX_TOOLWINDOW  = 0x00000080; // hide from alt-tab/taskbar
            const int WS_EX_NOACTIVATE  = 0x08000000; // never take foreground
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }
}

class TheaterContext : ApplicationContext
{
    readonly Settings cfg = Settings.Load();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    readonly List<OverlayForm> overlays = new();
    readonly WebRemote remote;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public TheaterContext()
    {
        BuildOverlays();

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "TheaterDim",
            Visible = true
        };
        tray.DoubleClick += (_, _) => { cfg.Enabled = !cfg.Enabled; cfg.Save(); RefreshMenu(); };

        // Web remote
        cfg.EnsureToken();
        remote = new WebRemote(cfg);
        StartRemote();

        RefreshMenu();

        // Rebuild overlays if monitors are added/removed/rearranged.
        SystemEvents.DisplaySettingsChanged += (_, _) => { BuildOverlays(); RefreshMenu(); };

        timer = new System.Windows.Forms.Timer { Interval = 300 };
        timer.Tick += Tick;
        timer.Start();
    }

    void BuildOverlays()
    {
        foreach (var o in overlays) o.Dispose();
        overlays.Clear();
        foreach (var scr in Screen.AllScreens)
        {
            var o = new OverlayForm { Bounds = scr.Bounds, Tag = scr.DeviceName };
            o.Opacity = cfg.DimPercent / 100.0;
            overlays.Add(o);
        }
    }

    void Tick(object? sender, EventArgs e)
    {
        bool active = false;
        string videoDevice = "";

        if (cfg.Enabled)
        {
            IntPtr h = GetForegroundWindow();
            if (h != IntPtr.Zero && IsTrigger(h) && IsFullscreen(h, out var scr))
            {
                active = true;
                videoDevice = cfg.AutoFollow || string.IsNullOrEmpty(cfg.MainDeviceName)
                    ? scr.DeviceName          // follow the monitor PotPlayer is on
                    : cfg.MainDeviceName;     // manual: keep chosen monitor bright
            }
        }

        UpdateOverlays(active, videoDevice);
    }

    bool IsTrigger(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Contains(cfg.TriggerProcess, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static bool IsFullscreen(IntPtr h, out Screen scr)
    {
        scr = Screen.FromHandle(h);
        if (!GetWindowRect(h, out var r)) return false;
        var b = scr.Bounds;
        const int tol = 2;
        return Math.Abs(r.L - b.Left) <= tol && Math.Abs(r.T - b.Top) <= tol
            && Math.Abs(r.R - b.Right) <= tol && Math.Abs(r.B - b.Bottom) <= tol;
    }

    void UpdateOverlays(bool active, string videoDevice)
    {
        double targetOpacity = cfg.DimPercent / 100.0;
        foreach (var o in overlays)
        {
            bool shouldDim = active && (string?)o.Tag != videoDevice;
            if (shouldDim)
            {
                if (Math.Abs(o.Opacity - targetOpacity) > 0.001) o.Opacity = targetOpacity;
                if (!o.Visible) o.Show();
            }
            else if (o.Visible)
            {
                o.Hide();
            }
        }
    }

    void StartRemote()
    {
        if (!cfg.RemoteEnabled) return;
        try { remote.Start(); }
        catch (Exception ex)
        {
            cfg.RemoteEnabled = false;
            tray.ShowBalloonTip(4000, "TheaterDim",
                $"Web remote failed on port {cfg.Port}: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    void RefreshMenu() => tray.ContextMenuStrip = BuildMenu();

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        var en = new ToolStripMenuItem("Enabled") { Checked = cfg.Enabled };
        en.Click += (_, _) => { cfg.Enabled = !cfg.Enabled; cfg.Save(); RefreshMenu(); };
        m.Items.Add(en);

        var dim = new ToolStripMenuItem("Dim level");
        foreach (int lvl in new[] { 30, 50, 70, 90 })
        {
            int l = lvl;
            var it = new ToolStripMenuItem($"{lvl}%") { Checked = cfg.DimPercent == lvl };
            it.Click += (_, _) => { cfg.DimPercent = l; cfg.Save(); BuildOverlays(); RefreshMenu(); };
            dim.DropDownItems.Add(it);
        }
        m.Items.Add(dim);

        var mode = new ToolStripMenuItem("Mode");
        var auto = new ToolStripMenuItem("Auto-follow PotPlayer") { Checked = cfg.AutoFollow };
        auto.Click += (_, _) => { cfg.AutoFollow = true; cfg.Save(); RefreshMenu(); };
        var man = new ToolStripMenuItem("Manual main display") { Checked = !cfg.AutoFollow };
        man.Click += (_, _) => { cfg.AutoFollow = false; cfg.Save(); RefreshMenu(); };
        mode.DropDownItems.Add(auto);
        mode.DropDownItems.Add(man);
        m.Items.Add(mode);

        var main = new ToolStripMenuItem("Main display") { Enabled = !cfg.AutoFollow };
        int idx = 1;
        foreach (var scr in Screen.AllScreens)
        {
            string dev = scr.DeviceName;
            string label = $"Display {idx} ({scr.Bounds.Width}x{scr.Bounds.Height}){(scr.Primary ? " ★Primary" : "")}";
            var it = new ToolStripMenuItem(label) { Checked = cfg.MainDeviceName == dev };
            it.Click += (_, _) => { cfg.MainDeviceName = dev; cfg.Save(); RefreshMenu(); };
            main.DropDownItems.Add(it);
            idx++;
        }
        m.Items.Add(main);

        m.Items.Add(new ToolStripSeparator());

        // --- Web remote ---
        var rem = new ToolStripMenuItem($"Web remote ({(remote.Running ? "on :" + cfg.Port : "off")})");

        var remOn = new ToolStripMenuItem("Enabled") { Checked = cfg.RemoteEnabled };
        remOn.Click += (_, _) =>
        {
            cfg.RemoteEnabled = !cfg.RemoteEnabled;
            cfg.Save();
            if (cfg.RemoteEnabled) StartRemote(); else remote.Stop();
            RefreshMenu();
        };
        rem.DropDownItems.Add(remOn);

        var showUrl = new ToolStripMenuItem("Show phone URL...");
        showUrl.Click += (_, _) =>
        {
            string url = remote.Url();
            Clipboard.SetText(url);
            MessageBox.Show(
                $"Open on your phone (same WiFi):\n\n{url}\n\n(Copied to clipboard.)\nKeep this link private — it controls this PC.",
                "TheaterDim Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        rem.DropDownItems.Add(showUrl);

        var regen = new ToolStripMenuItem("Regenerate token");
        regen.Click += (_, _) =>
        {
            cfg.Token = "";
            cfg.EnsureToken();
            RefreshMenu();
        };
        rem.DropDownItems.Add(regen);

        m.Items.Add(rem);

        m.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();
        m.Items.Add(exit);

        return m;
    }

    void ExitApp()
    {
        timer.Stop();
        remote.Stop();
        foreach (var o in overlays) o.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}
