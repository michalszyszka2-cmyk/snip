# Snip

A tiny Windows snipping tool that lives in the tray. Press **Ctrl + ' + #** together, drag a box, and that part of the screen is on your clipboard. Paste it anywhere with Ctrl+V.

- No window and nothing to switch on: it starts with Windows and waits in the tray.
- The screen freezes and dims while you drag, so menus and tooltips stay put.
- Snips are shrunk to at most 1568 px on the long side, which keeps big ones small (a full 4K screen comes out around 0.3 MB). That's the size Claude and most chat tools scale images down to anyway.
- Nothing is saved to disk. **Esc** cancels.

## Install

1. Download **snip.exe** from the [latest release](../../releases/latest).
2. Double-click it. It installs itself to `%LOCALAPPDATA%\Programs\Snip`, adds a Startup and a Desktop shortcut, and starts.

No admin rights needed. Windows 10 or 11.

Because the exe isn't code-signed, Windows may show **"Windows protected your PC"** the first time. Click **More info → Run anyway**. Antivirus may also scan it for a few seconds before it starts.

## Use

| | |
|---|---|
| **Ctrl + ' + #** | Start a snip, then drag a box |
| **Esc** | Cancel |
| Right-click the tray camera | Snip now / Quit / Uninstall |

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
