# ConnectMe

Use an office iMac (27", Late 2013, 2560×1440) as a **wireless screen for a Windows laptop**: either a mirror of the laptop screen or a true extended second monitor. It runs only on the office network; no internet servers are involved.

```
 Windows laptop                                     iMac 27"
 ┌───────────────────────────────┐  TCP 47800 (LAN) ┌─────────────────────────────────────┐
 │ ConnectMe.exe                 │ ───────────────▶ │ macOS   → ConnectMe Display.app      │
 │  virtual monitor (extend)     │   H.264 video    │ Windows → ConnectMeDisplay.exe + mpv │
 │  FFmpeg: capture + GPU encode │                  │ Linux   → ConnectMeDisplay + mpv     │
 └───────────────────────────────┘                  └─────────────────────────────────────┘
          finds iMacs via mDNS (_connectme._tcp) — or type the IP shown on the iMac
```

The office has 20 laptops and 20 iMacs: 10 iMacs on macOS, 5 on Windows and 5 on Kali Linux.

## Install (one command per machine)

Each command installs **or updates** to the latest release. Re-run it after a new version comes out.

**Kali / Linux iMac:** open a Terminal and run:
```bash
curl -fsSL https://raw.githubusercontent.com/Kaissabbegh/ConnectMe/main/install/linux.sh | bash -s -- "Desk 1 iMac"
```
It installs mpv if needed, sets the app to start at login, and prints the command to start it now. X11 and Wayland both work. In the desktop's power settings, set screen blanking and auto-lock to **Never**.

**Windows iMac (Boot Camp):** in PowerShell **as Administrator**:
```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/Kaissabbegh/ConnectMe/main/install/imac-windows.ps1))) -Name "Desk 12 iMac"
```
It installs to Program Files with mpv, opens the firewall (private/domain networks only), and starts at login. The office Wi-Fi must be **Private** in Windows, not Public.

**macOS iMac (10.15+):** in Terminal:
```bash
curl -fsSL https://raw.githubusercontent.com/Kaissabbegh/ConnectMe/main/install/mac.sh | bash
```
It builds the app on the iMac (Apple's Command Line Tools are installed first if missing) and puts it in Applications. Allow incoming connections when asked.

**Windows laptop:** in PowerShell (no admin needed):
```powershell
irm https://raw.githubusercontent.com/Kaissabbegh/ConnectMe/main/install/laptop.ps1 | iex
```
It installs ConnectMe and FFmpeg into `%LOCALAPPDATA%\ConnectMe`, with Desktop and Start menu shortcuts.
- For an **extended screen**, also install the [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases) and set the new screen to **2560 × 1440**.

Each iMac shows a full-screen page with its name, IP address and a **4-digit pairing code**.

**Stopping the receiver:** press `q` on the iMac's screen.
- Linux: `pkill -f ConnectMeDisplay`. The `-f` is needed because Linux truncates process names to 15 characters.
- Windows: Task Manager → ConnectMeDisplay → End task.
- If the screen is stuck on Linux: press Ctrl+Alt+F3, log in, run `pkill -f ConnectMeDisplay; pkill mpv`, then return with Ctrl+Alt+F1 (or F2/F7).

**Receiver options** (Windows/Linux): `--name "Desk 1 iMac"`, `--vo <mpv video output>`, `--hwdec <auto-safe|no>`.
- Linux defaults to software decoding, and to `--vo wlshm` under Wayland, because the 2013 iMacs' NVIDIA graphics garble mpv's GPU output.

## Daily use

1. Open ConnectMe on the laptop. Your iMac appears in the list; if not, type the IP shown on the iMac.
2. Type the code shown on the iMac.
3. Pick the screen: **main screen** mirrors the laptop, the **virtual display** gives a second monitor.
4. Click **Connect**. Click **Disconnect** when done; the iMac then shows a new code.

## Development

| Path | What |
|---|---|
| `dotnet/ConnectMe.Sender/` | Laptop app (C# / .NET 8 WinForms) |
| `dotnet/ConnectMe.Display/` | iMac receiver for Windows + Linux (.NET 8, video shown by mpv) |
| `dotnet/ConnectMe.TestReceiver/` | Console stand-in receiver for testing without an iMac |
| `mac/ConnectMeDisplay/` | iMac receiver for macOS (Swift; `build.sh`) |
| `install/` | The one-line installers above |
| `docs/PROTOCOL.md` | Wire protocol shared by all apps |
| `RUSTDESK_NOTES.md` | Background study of RustDesk |

- **Build:** `cd dotnet && dotnet build -c Release` (.NET 8 SDK)
- **Local packages:** `powershell -ExecutionPolicy Bypass -File dotnet\publish.ps1` builds into `dist/`. Then add `ffmpeg.exe` to `dist/laptop` and `mpv.exe` to `dist/imac-windows`.
- **Release:** push a tag such as `v0.1.1`. GitHub Actions (`.github/workflows/release.yml`) builds `ConnectMe.exe`, `ConnectMeDisplay-win-x64.exe` and `ConnectMeDisplay-linux-x64` and publishes them; the installers always fetch the latest release.
- **Test without an iMac:** run `dotnet run --project ConnectMe.TestReceiver -- 1234`, connect the laptop app to `127.0.0.1` with code `1234`, then `ffplay received.h264`.
- **Logs:**
  - Windows/Linux receiver: `display.log` and `mpv.log` in `%LOCALAPPDATA%\ConnectMe\` or `~/.local/share/ConnectMe/`
  - Laptop app: shown in its window

## How it works

- **Capture and encode (laptop):** FFmpeg `ddagrab` captures the chosen screen. The first time, the app tries NVIDIA NVENC → AMD AMF → Intel Quick Sync → CPU x264 and keeps the first that works.
- **Stream:** H.264 (standard limited-range BT.709) at 30/60 fps, no B-frames, a keyframe every second, over TCP. If the network falls behind, the laptop drops the backlog and resumes at the next keyframe, so latency stays low.
- **Display:**
  - macOS: `AVSampleBufferDisplayLayer` (hardware decode).
  - Windows/Linux: one full-screen mpv window (low-latency profile, hardware decode when available). It shows the pairing text while idle and plays the stream from a loopback socket during a session.
- **Discovery:** mDNS `_connectme._tcp`. macOS uses Bonjour; the Windows/Linux receiver answers queries itself, sharing UDP 5353 with the system's responder.
- **Pairing:** a random 4-digit code that changes after every session and after 5 wrong attempts. One laptop per iMac at a time.

## Known limits (v0.1)

- **No encryption yet:** video crosses the office LAN unencrypted. Planned for v2 (see `docs/PROTOCOL.md`).
- No keyboard/mouse from the iMac to the laptop, and no audio.
- If the company Wi-Fi has **client isolation**, laptops can't reach iMacs. Ask IT, or plug the iMacs into Ethernet (recommended anyway).
- On Linux iMacs (NVIDIA Kepler + nouveau), video is decoded and drawn on the CPU (`wlshm`). Use the "Standard" or "Busy Wi-Fi" quality.
- Laptops convert colours on the CPU before encoding (for compatibility). A typical i5 laptop manages 1080p at 30 fps.
- The virtual display must be on the laptop's primary graphics adapter (true on almost all laptops).
