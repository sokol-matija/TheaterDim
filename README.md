# TheaterDim

Windows tray app for PotPlayer. Two features:

1. **Theater dimming** — when PotPlayer goes fullscreen on one monitor, black overlays dim the *other* monitors. Auto-detects all monitors (2, 3, n).
2. **Web remote** *(planned)* — small mobile web page served on localhost / LAN / Tailscale. Buttons fire play/pause, volume, seek at PotPlayer. No app install on the phone — just a browser.

## Stack

| Part | Tech |
|------|------|
| Tray + overlays | C# .NET 9, WinForms (`NotifyIcon` + per-monitor click-through layered `Form`) |
| Fullscreen detect | Win32 `GetForegroundWindow` + `GetWindowRect` vs `Screen.Bounds`, 300 ms poll |
| Web remote server | Embedded `HttpListener` in same process, background thread |
| PotPlayer control | `SendMessage(hwnd, WM_COMMAND, id, 0)` to `ahk_class PotPlayer64` |
| Remote frontend | One static `index.html`, vanilla JS, mobile-first |

One process, one `.exe`. No external services.

## PotPlayer control reference

`SendMessage(hWnd, 0x0111 /*WM_COMMAND*/, ID, 0)` — target window class `PotPlayer64`.
SendMessage works without focus; player may be hidden.

| Action | ID | Action | ID |
|--------|------|--------|------|
| Play/Pause toggle | 10014 | Volume Up | 10035 |
| Play | 20001 | Volume Down | 10036 |
| Pause | 20000 | Toggle Mute | 10037 |
| Stop | 20002 | Next | 10124 |
| Previous | 10123 | Toggle Subtitles | 10126 |
| Open File | 10158 | Toggle Playlist | 10011 |
| Fullscreen | 10013 | Toggle OSD/info | 10351 |
| Seek −5s | 10059 | Seek +5s | 10060 |
| Seek −30s | 10061 | Seek +30s | 10062 |

Sources: AutoHotkey PotPlayer x64 library; Unified Remote PotPlayer remote.lua.

## Build / run

```powershell
dotnet run -c Release
# or single exe:
dotnet publish -c Release -r win-x64
```

## Roadmap

- [x] Tray app + multi-monitor dimming (auto-follow + manual main display)
- [x] Embedded web remote (play/pause, vol ±, seek ±5/±30, fullscreen, subs, playlist)
- [x] Auth token on remote endpoints
- [ ] Live state (current time / volume) via SSE
- [ ] Autostart logon task
