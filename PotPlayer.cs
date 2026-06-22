using System.Runtime.InteropServices;
using System.Text;

namespace TheaterDim;

// Controls a running PotPlayer via WM_COMMAND. No focus required; window may be hidden.
static class PotPlayer
{
    const uint WM_COMMAND = 0x0111;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);

    [DllImport("user32.dll")]
    static extern int GetWindowTextLength(IntPtr hWnd);

    // 64-bit class is PotPlayer64; try fallbacks for other builds.
    static readonly string[] Classes = { "PotPlayer64", "PotPlayer", "PotPlayerMini64", "PotPlayerMini" };

    // Verified WM_COMMAND IDs (AutoHotkey PotPlayer x64 library).
    public static readonly Dictionary<string, int> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playpause"] = 10014,
        ["play"]      = 20001,
        ["pause"]     = 20000,
        ["stop"]      = 20002,
        ["next"]      = 10124,
        ["prev"]      = 10123,
        ["volup"]     = 10035,
        ["voldown"]   = 10036,
        ["mute"]      = 10037,
        ["subs"]      = 10126,
        ["playlist"]  = 10011,
        ["osd"]       = 10351,
        ["fullscreen"]= 10013,
        ["seekback5"] = 10059,
        ["seekfwd5"]  = 10060,
        ["seekback30"]= 10061,
        ["seekfwd30"] = 10062,
    };

    static IntPtr Find()
    {
        foreach (var c in Classes)
        {
            var h = FindWindow(c, null);
            if (h != IntPtr.Zero) return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>Send a raw command ID. Returns false if PotPlayer not found.</summary>
    public static bool Send(int commandId)
    {
        var h = Find();
        if (h == IntPtr.Zero) return false;
        SendMessage(h, WM_COMMAND, (IntPtr)commandId, IntPtr.Zero);
        return true;
    }

    /// <summary>Send a named command (see Commands). Returns false if unknown or not running.</summary>
    public static bool Send(string name)
        => Commands.TryGetValue(name, out int id) && Send(id);

    public static bool IsRunning => Find() != IntPtr.Zero;

    /// <summary>Window handle of the running PotPlayer, or IntPtr.Zero.</summary>
    public static IntPtr Handle() => Find();

    /// <summary>Current PotPlayer window title (usually the playing media), "" if not running.</summary>
    public static string Title()
    {
        var h = Find();
        if (h == IntPtr.Zero) return "";
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        string t = sb.ToString();
        int i = t.LastIndexOf(" - PotPlayer", StringComparison.OrdinalIgnoreCase);
        if (i > 0) t = t.Substring(0, i);
        return t.Trim();
    }
}
