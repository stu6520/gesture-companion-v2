# Gesture Companion v2

Gesture Companion adds customizable touch gestures to PaintTool SAI 1 and PaintTool SAI 2. It runs in the Windows system tray, listens for Windows pointer touch input, and connects the correct 32-bit or 64-bit bridge automatically.

This repository contains **source code only**. It does not provide a prebuilt EXE, DLL, ZIP, or installer. You can build a local copy by following the steps below.

## Important security disclosure

Gesture Companion uses a native DLL bridge loaded into the active `sai.exe` or `sai2.exe` process. The bridge receives touch messages inside the target process and sends the configured keyboard or drag actions to its canvas.

Process injection is security-sensitive behavior and may be flagged by Windows or antivirus software even when built from this source. Review the implementation in `src/Injector` and `src/NativePayload` before running it. Do not download unofficial prebuilt copies from third parties, and do not disable antivirus protection globally.

## Requirements

- Windows 10 or Windows 11, 64-bit
- PaintTool SAI 1 or PaintTool SAI 2
- A multi-touch display or tablet that provides Windows pointer touch input
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 or newer with **Desktop development with C++**
- The MSVC x86/x64 build tools and a Windows SDK

## Build and run

### 1. Clone the repository

```powershell
git clone https://github.com/stu6520/gesture-companion-v2.git
cd gesture-companion-v2
```

### 2. Build a local portable copy

Open PowerShell in the repository folder and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-portable.ps1 -Version 2.0.0
```

The script builds:

- The 64-bit tray host
- An x86 injector and payload for SAI 1
- An x64 injector and payload for SAI 2
- A local portable folder and ZIP under `release`

Generated files under `release`, `bin`, `obj`, `Debug`, and `Release` are intentionally ignored by Git.

### 3. Run the local build

```powershell
.\release\GestureCompanion-v2.0.0-win-x64-portable\GestureCompanion.exe
```

The app has no main window. Its icon appears in the Windows notification area. Open SAI, focus its canvas, and try a gesture. Double-click the tray icon to open Settings, or right-click it for Settings, Start with Windows, and Quit.

Do not move `GestureCompanion.exe` away from its generated `Bridge` folder.

## Default gestures

| Gesture | Default action |
| --- | --- |
| Pinch open | `PageUp` |
| Pinch close | `PageDown` |
| Rotate clockwise | `Shift+PageDown` |
| Rotate counter-clockwise | `Shift+PageUp` |
| Two-finger drag | `Space` + left-button drag |
| One-finger slide | Uses Pan behavior |
| One-finger press and hold | Right-click |
| Two-finger tap | `Ctrl+Z` |
| Three-finger tap | `Ctrl+Y` |
| Four-finger tap | `Home` |

Hotkeys, gesture modes, sensitivity, and speed can be changed from the tray Settings window.

## SAI 1 and SAI 2

No version selection or launch order is required. Gesture Companion watches the foreground application and uses:

- The x86 bridge for `sai.exe`
- The x64 bridge for `sai2.exe`

Both applications may remain open. Gestures are enabled for the supported application currently in the foreground. Failed bridge connections are retried automatically.

## Troubleshooting

If gestures do not respond:

1. Confirm Gesture Companion is present in the notification area.
2. Confirm SAI is the foreground application.
3. Confirm the complete generated `Bridge` folder remains beside the EXE.
4. Check `%LOCALAPPDATA%\GestureCompanion\bridge.log`.

If you would like help, you are welcome to open a GitHub issue.

If the build script cannot find MSBuild, install the Visual Studio **Desktop development with C++** workload. The script currently detects common Visual Studio 2022 and newer Community/Build Tools locations.

## Repository layout

```text
src/Host/           Tray host and settings interface
src/Injector/       Architecture-matched bridge loader
src/NativePayload/  Native in-process touch bridge
 docs/               User documentation and license
build-portable.ps1  Local source-build and packaging script
```

## License and trademark notice

See [docs/LICENSE](docs/LICENSE) before using or modifying this source.

Gesture Companion is an independent utility and is not affiliated with or endorsed by SYSTEMAX or the developers of PaintTool SAI. PaintTool SAI is referenced only to describe compatibility.
