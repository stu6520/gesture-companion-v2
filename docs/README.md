# Gesture Companion v2.0.0

Gesture Companion adds customizable touch gestures to PaintTool SAI 1 and PaintTool SAI 2. It runs quietly in the Windows system tray, detects the supported application in the foreground, and connects the correct 32-bit or 64-bit bridge automatically.

## Requirements

- Windows 10 or Windows 11, 64-bit
- PaintTool SAI 1 or PaintTool SAI 2
- A multi-touch display or tablet that provides Windows pointer touch input

Gesture Companion handles finger touch. Pen and mouse input remain available to the drawing application.

## Install and run

1. Extract the entire ZIP to a permanent folder.
2. Keep `GestureCompanion.exe`, `icon.ico`, and the `Bridge` folder together.
3. Run `GestureCompanion.exe`.
4. Open SAI and bring its canvas to the foreground.
5. Use the gestures on your touch device.

The app has no main window. Look for the Gesture Companion icon in the notification area. Double-click the tray icon to open Settings, or right-click it for Settings, Start with Windows, and Quit.

Do not run the EXE from inside the ZIP. If Windows blocks the download, open the ZIP or EXE Properties, select Unblock when available, and extract it again.

## Default gestures

| Gesture | Default action |
| --- | --- |
| Pinch open | `PageUp` |
| Pinch close | `PageDown` |
| Rotate clockwise | `Shift+PageDown` |
| Rotate counter-clockwise | `Shift+PageUp` |
| Two-finger drag | `Space` + left-button drag |
| One-finger slide | Uses the Pan behavior |
| One-finger press and hold | Right-click |
| Two-finger tap | `Ctrl+Z` |
| Three-finger tap | `Ctrl+Y` |
| Four-finger tap | `Home` |

These shortcuts can be changed in Settings. Clear a keyboard mapping to disable that mapped action. When Pan or Right click mode is enabled for a one-finger gesture, its keyboard mapping is ignored.

## Settings

### Key mappings

Click a hotkey field and press the desired keyboard key or key combination. Mouse buttons and mouse-wheel input are not recorded as hotkeys.

- Pan drag sends the configured key while performing an in-process left-button drag.
- One-finger slide can inherit the Pan configuration.
- One-finger hold can perform one right-click instead of using its keyboard mapping with left-button hold.
- Two-, three-, and four-finger taps have independent mappings.

### Response controls

- Zoom Sensitivity controls how much pinch movement is required for each zoom step.
- Zoom Speed controls the rate of repeated zoom actions.
- Rotate Sensitivity controls how much rotation is required for each rotate step.
- Rotate Speed controls the rate of repeated rotate actions.
- Pan Sensitivity controls how much movement is required before pan begins.

The displayed values are percentages: move toward High or Fast for a more responsive gesture.

### Settings buttons

- Reset restores the values that were saved when the window opened.
- Default loads the factory defaults without saving them yet.
- Apply saves the displayed settings and closes the window.
- Close discards unsaved changes and closes the window.

Settings are stored in `%APPDATA%\GestureCompanion\pointer-host-settings.json`.

## SAI 1 and SAI 2

No version selection is required. Gesture Companion supports `sai.exe` and `sai2.exe` and starts the matching bridge when either application becomes the foreground window. Both applications may remain open; each receives gestures while it is active.

Start order does not matter. Gesture Companion retries the connection automatically if SAI is opened later or if the first bridge attempt fails.

## Troubleshooting

### Gestures do not respond

1. Confirm Gesture Companion is visible in the system tray.
2. Bring SAI to the foreground and touch its canvas.
3. Keep the complete `Bridge` folder beside `GestureCompanion.exe`.
4. Close and reopen Gesture Companion if security software blocked its first connection.
5. Check `%LOCALAPPDATA%\GestureCompanion\bridge.log` for connection details.

Gesture Companion only sends gestures while SAI 1 or SAI 2 is the foreground application.

### Windows or security software shows a warning

Gesture Companion loads its included bridge into the active SAI process so commands reach the correct canvas. This behavior can attract warnings from Windows or antivirus software. Only use a package obtained from the official distribution source, and do not replace files in the `Bridge` folder.

### The tray icon is hidden

Open the Windows notification-area overflow menu and drag Gesture Companion onto the visible tray area if desired.

## Portable package contents

- `GestureCompanion.exe`: tray application and settings
- `icon.ico`: tray icon
- `Bridge\win-x86`: SAI 1 bridge components
- `Bridge\win-x64`: SAI 2 bridge components
- `QUICKSTART.md`: short setup guide
- `README.md`: complete usage guide
- `LICENSE`: license terms

## Notice

Gesture Companion is an independent utility and is not affiliated with or endorsed by SYSTEMAX or the developers of PaintTool SAI. PaintTool SAI is referenced only to describe compatibility.
