# Snip

A tiny Windows snipping tool that lives in the tray. Press **Ctrl + ' + #** together, drag a box, and that part of the screen is on your clipboard. Paste it anywhere with Ctrl+V. Hold **Ctrl** while you drag and a red frame appears round the box with a small bar above it: press **Record**, talk, press the same button (now **Stop & save**), and the video is in Downloads.

- No window and nothing to switch on: it starts with Windows and waits in the tray.
- The screen freezes and dims while you drag, so menus and tooltips stay put.
- Snips are shrunk to at most 1568 px on the long side, which keeps big ones small (a full 4K screen comes out around 0.3 MB). That's the size Claude and most chat tools scale images down to anyway.
- Snips are never saved to disk. **Esc** cancels.
- Recordings go to your **Downloads** folder as `recording-YYYY-MM-DD-HHMM.mp4` (H.264 + AAC, 30 fps, playable anywhere). The frame stays round the area while recording with a timer; nothing of it is in the video. Drag the frame (its bar or red edge) before or during recording to move the box: the video follows it, and the camera moves with it. Stop with the frame's button, Alt+S, a click on the red tray dot, or the tray panel. The video is also copied, so Ctrl+V pastes it; click the "Saved" message to open Downloads with it selected.
- **Camera, like Loom:** your webcam shows in the corner of the recording as a circle, a rounded square or a rounded 16:9 picture. Drag it anywhere, double-click it to change its size, right-click it to change its shape or background, or hide it with the camera button on the bar.
- **iPhone as a second camera:** turn on *iPhone camera* in the tray panel, then open `https://<your-pc>.local:8787` in Safari on the phone (the panel shows the exact address) and tap *Use this camera*. Its picture appears as a smaller second bubble beside the webcam, and both are in the recording. The phone sends over your Wi-Fi to Snip only; nothing is saved on the phone. First time only: open `http://<your-pc>.local:8788` on the phone, download Snip's certificate, install it from Settings, and switch it on under General › About › Certificate Trust Settings - Safari only allows the camera on a secure page. Windows Defender Firewall must allow snip.exe on private networks (it asks the first time you turn the iPhone camera on). The iPhone link stays closed until you turn it on. While it is on, anyone on the same network can reach it, with no password, so only use it on Wi-Fi you trust, and turn it off afterwards.
- **Background blur or picture:** blur the room behind you, or swap it for a picture of your choice. An AI model (Robust Video Matting) finds you in the picture and follows you from frame to frame, run by the machine-learning engine built into Windows (on the graphics chip when it can), so nothing is uploaded. The model is downloaded once, with your say-so (about 15 MB, kept in Snip's own folder). *Record just the camera* in the tray panel records just you.
- **Pause, resume, start over.** Recording starts after a 3, 2, 1 countdown. Pause leaves the pause out of the video; Start over throws the take away and gets you ready again.
- Recording uses [FFmpeg](https://ffmpeg.org/). If it isn't on the computer, Snip offers to download it the first time you record (about 115 MB, kept in Snip's own folder).

## Install

1. Download **snip.exe** from the [latest release](../../releases/latest).
2. Double-click it. It installs itself to `%LOCALAPPDATA%\Programs\Snip`, adds a Startup and a Desktop shortcut, and starts.

No admin rights needed. Windows 10 or 11. It shows up in **Settings > Apps > Installed apps**, and that is where you uninstall it.

Because the exe isn't code-signed, Windows may show **"Windows protected your PC"** the first time. Click **More info → Run anyway**. Antivirus may also scan it for a few seconds before it starts.

## Use

| | |
|---|---|
| **Ctrl + ' + #** | Start a snip, then drag a box |
| **Ctrl** while dragging | Frame that box for recording; then press **Record** on the frame |
| **Stop & save** on the frame, or **Alt+S** | Stop the recording and save it |
| Pause / Start over / camera icons on the bar | Pause or resume, throw the take away and start again, show or hide the camera bubble |
| **✕** on the frame | Close; while recording, asks before throwing the recording away |
| **Esc** | Cancel |
| Click the tray camera | One panel with everything on it: Snip now, Record an area (or the whole screen), the camera, its shape and background, the microphone, the recordings folder, Quit. While recording it shows the timer, Pause and Stop & save. |

The panel has no submenus: pick the webcam (or "No camera"), its shape, and the background - none, blur, a ready-made picture or your own - and the choice applies at once. *Record just the camera* records only you. The **Microphone** list is what Windows sees; the first one is used until you pick another, and "No microphone" records silent video. Computer sound (what the speakers play) is not recorded. Esc or a click elsewhere closes the panel.

The shortcut uses the `'` and `#` keys of a UK keyboard (next to Enter). On other layouts it uses whichever keys type `'` and `#`. You can press them in either order. While Snip runs, Ctrl + # on its own does nothing in other apps.

## Build from source

Needs nothing beyond Windows: the C# compiler ships with the .NET Framework.

```
build.cmd
```

produces `snip.exe` from `snip.cs`, `phone.cs`, `phone.html` and `camera.ico`.

## Credits

Camera icon: [Microsoft Fluent UI Emoji](https://github.com/microsoft/fluentui-emoji), MIT licence.

Background model: [Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting) by Shanchuan Lin et al. (ByteDance), GPL-3.0 licence. It is not part of this program: Snip downloads `rvm_mobilenetv3_fp32.onnx` from the authors' GitHub release the first time you switch a background effect on.

## Licence

MIT. See [LICENSE](LICENSE).
