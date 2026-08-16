# Kodi Remote

A small Windows desktop companion app that lets an infrared-to-USB keyboard
unit control [Kodi](https://kodi.tv/) even while Kodi itself has keyboard
focus, and shows a live on-screen status display (zoom level, subtitles,
playback position) for the currently playing item.

## Rationale

Kodi doesn't expose every one of its functions as a keyboard shortcut, and we
wanted a way to extend its key bindings without touching Kodi's own source.

The IR unit is a USB device that emulates a keyboard. It already works fine
with Kodi, but only for keys Kodi has hardcoded shortcuts for. KodiRemote
fills that gap by registering global hotkeys for the extra functions we
needed, and it adds a live status display for the currently playing item,
handy when Kodi runs on a second screen such as a TV.

In our own setup, a Surface Go 3 tablet drives Kodi on a TV while the
tablet's own screen shows Kodi Remote's status. The IR unit plugs into the
tablet, and Kodi Remote runs there too.

Keypresses from the IR receiver go to whichever window currently has focus,
and since Kodi is normally that foreground app, it's the only one that sees
them. To work around this, Kodi Remote registers **global hotkeys** through
the Win32 API, letting it catch specific key combinations no matter which
window is active, then relays the matching commands to Kodi via its
JSON-RPC API. In the background, it also keeps polling Kodi so the status
overlay (meant for a second screen) stays current.

## Features

- Global hotkeys (work even when Kodi has focus):
  - `Ctrl+Shift+Alt+F1` / `F2` — zoom video out / in (0.01x steps)
  - `Ctrl+Shift+Alt+F3` / `F4` — previous / next subtitle track
  - `Ctrl+Shift+Alt+F5` — toggle subtitles on/off
- Live status display: zoom factor, active subtitle, playback position,
  now-playing title, and end time of the current item.
- Tap the status header to toggle Kodi between windowed and fullscreen
  mode (handy for touch screens without a keyboard).
- Automatically restores focus to Kodi when it is running on the same
  machine, so the IR unit keeps working even if another window steals
  focus.
- Built-in debug log panel showing requests sent to, and responses
  received from, Kodi's JSON-RPC API.

## Requirements

- Windows with [.NET 10](https://dotnet.microsoft.com/) (WPF, `net10.0-windows`).
- A running Kodi instance with the **web server / JSON-RPC control**
  enabled (Settings → Services → Control) and a username/password
  configured.

## Configuration

On first run, Kodi Remote creates a `settings.json` file next to the
executable with the following defaults:

```json
{
  "HostUrl": "http://localhost:8080/jsonrpc",
  "Username": "kodi",
  "Password": ""
}
```

Edit this file to match your Kodi instance's address and credentials,
then restart the app. If the file is missing or unreadable, defaults are
used and the corrupt file is renamed to a `.bak` file for inspection.

> The app is intended for use on a local/trusted network; credentials are
> stored in plain text in `settings.json`.

## Building & running

```powershell
dotnet build
dotnet run
```

## Publishing a release build

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The published executable is produced under
`bin\Release\net10.0-windows\win-x64\publish\KodiRemote.exe`. See
`release.ps1` for the release command.

## Project layout

| File | Purpose |
|---|---|
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Main window UI, global hotkey handling, polling loop, focus management |
| `KodiClient.cs` | JSON-RPC client for talking to Kodi (requests, batching, error handling) |
| `KodiSettings.cs` | Loading/saving of `settings.json` |
| `SubtitleTrackItem.cs` | View model item for a subtitle track entry |
| `App.xaml` / `App.xaml.cs` | Application entry point |

See `CHANGELOG.md` for a history of notable changes.
