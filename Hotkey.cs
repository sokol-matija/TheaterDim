using System.Runtime.InteropServices;

namespace TheaterDim;

// Global hotkey via RegisterHotKey. Hidden message window receives WM_HOTKEY
// on the UI thread (created from the UI thread), so the handler is UI-safe.
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const int HOTKEY_ID = 0xB001;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? Pressed;
    public bool Registered { get; }

    public HotkeyWindow(uint mods, uint vk)
    {
        CreateHandle(new CreateParams());
        Registered = RegisterHotKey(Handle, HOTKEY_ID, mods | MOD_NOREPEAT, vk);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID)
            Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(Handle, HOTKEY_ID);
        DestroyHandle();
    }
}
