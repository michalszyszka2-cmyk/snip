# Snip

A tiny Windows snipping tool that lives in the tray. Press **Ctrl + ' + #** together, drag a box, and that part of the screen is on your clipboard. Paste it anywhere with Ctrl+V. Hold **Shift** while you drag and a red frame with **Record** and **Stop & save** buttons appears round the box: press Record, talk, press Stop, and the video is in Downloads.

- No window and nothing to switch on: it starts with Windows and waits in the tray.
- The screen freezes and dims while you drag, so menus and tooltips stay put.
- Snips are shrunk to at most 1568 px on the long side, which keeps big ones small (a full 4K screen comes out around 0.3 MB). That's the size Claude and most chat tools scale images down to anyway.
- Snips are never saved to disk. **Esc** cancels.
- Recordings go to your **Downloads** folder as `recording-YYYY-MM-DD-HHMM.mp4` (H.264 + AAC, 30 fps, playable anywhere). The frame stays round the area while recording with a timer; nothing of it is in the video. Stop with the frame's button, Alt+S, a click on the red tray dot, or the tray menu; Explorer then opens Downloads with the new file selected.
- Recording uses [FFmpeg](https://ffmpeg.org/). If it isn't on the computer, Snip offers to download it the first time you record (about 115 MB, kept in Snip's own folder).

## Install

1. Download **snip.exe** from the [latest release](../../releases/latest).
2. Double-click it. It installs itself to `%LOCALAPPDATA%\Programs\Snip`, adds a Startup and a Desktop shortcut, and starts.

No admin rights needed. Windows 10 or 11.

Because the exe isn't code-signed, Windows may show **"Windows protected your PC"** the first time. Click **More info → Run anyway**. Antivirus may also scan it for a few seconds before it starts.

## Use

| | |
|---|---|
| **Ctrl + ' + #** | Start a snip, then drag a box |
| **Shift** while dragging | Frame that box for recording; then press **Record** on the frame |
| **Stop & save** on the frame, or **Alt+S** | Stop the recording and save it |
| **✕** on the frame | Cancel (or stop, if recording) |
| **Esc** | Cancel |
| Right-click the tray camera | Snip now / Record an area / Record the whole screen / Microphone / Open the recordings folder / Quit / Uninstall |

The **Microphone** menu lists what Windows sees; the first one is used until you pick another, and "No microphone" records silent video. Computer sound (what the speakers play) is not recorded.

The shortcut uses the `'` and `#` keys of a UK keyboard (next to Enter). On other layouts it uses whichever keys type `'` and `#`.

## Build from source

Needs nothing beyond Windows: the C# compiler ships with the .NET Framework.

```
build.cmd
```

produces `snip.exe` from `snip.cs` and `camera.ico`.

## Credits

Camera icon: [Microsoft Fluent UI Emoji](https://github.com/microsoft/fluentui-emoji), MIT licence.

## Licence

MIT. See [LICENSE](LICENSE).
