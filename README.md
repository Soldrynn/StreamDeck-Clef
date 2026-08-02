# Clef for Stream Deck +

![Clef app icon](com.davedev.clef.sdPlugin/assets/plugin-icon.png)

[![Windows 11](https://img.shields.io/badge/Windows-11-555555)](https://www.microsoft.com/windows/windows-11)
[![Stream Deck +](https://img.shields.io/badge/Stream%20Deck-%2B-555555)](https://www.elgato.com/us/en/p/stream-deck-plus-black)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-555555)](LICENSE)

Clef controls Apple Music for Windows from the dials and touch strip on an
Elgato Stream Deck +. Use Clef to skip tracks, play or pause, adjust only the
app's volume, mute the app, and view live track information. Clef uses direct
Windows APIs instead of keyboard shortcuts or simulated clicks.

## Features

Clef provides the following features:

- Native playback control through Windows media-session APIs.
- Per-app volume and mute control that does not change the system volume.
- Automatic recovery when Apple Music for Windows, Stream Deck, or Windows
  restarts.
- Resilient volume binding when Windows renames `Amp Library Agent`, including
  numbered variants.
- Live title, artist, progress, volume, mute, and connection feedback.
- Album artwork with protection against delayed covers appearing on the wrong
  track.
- Independent settings for each Volume Controls action.
- A transparent, grayscale liquid-glass design for the Stream Deck + touch
  strip.

## Requirements

To use Clef, you need the following software and hardware:

- Windows 11 on an x64 computer.
- Elgato Stream Deck 7.1 or later.
- Elgato Stream Deck +.
- Apple Music for Windows.

Clef does not support Windows on ARM64. The installer includes a self-contained
Windows helper, so plugin users don't need to install .NET.

## Install Clef

To install Clef, do the following:

1. Download `com.davedev.clef.streamDeckPlugin` from the
   [latest GitHub release](https://github.com/Soldrynn/StreamDeck-Clef/releases/latest).
2. Double-click the downloaded file.
3. In Stream Deck, approve the installation.
4. In the Stream Deck action list, expand the **Clef** category.
5. Drag **Playback Controls** or **Volume Controls** onto a Stream Deck + dial.
6. Open Apple Music for Windows and play a song for a few seconds.

Clef starts its local Windows helper automatically. Playing a song once lets
Windows create the media and audio sessions that Clef controls.

## Use the controls

Both actions use the same interaction pattern:

| Action | Rotate left | Rotate right | Press or touch |
| --- | --- | --- | --- |
| Playback Controls | Previous track | Next track | Play or pause |
| Volume Controls | Lower app volume | Raise app volume | Mute or unmute the app |

To change the volume step, select **Volume Controls** in Stream Deck, and then
choose a value from 1% to 10%. The default step is 2%.

## Interpret the touch-strip status

The touch strip reports the following information and states:

- **Playback Controls:** Song title, artist, playback status, elapsed time,
  progress, and available album artwork.
- **Volume Controls:** Current Apple Music for Windows volume and mute state.
- **Unavailable:** Windows cannot find the matching media or audio session.
- **Reconnecting:** The helper is recovering from an app, device, or Windows
  state change.

Apple Music for Windows can delay album artwork after a track change. If a
dial-initiated skip still has no valid cover after 1.25 seconds, Clef briefly
pauses and resumes playback to refresh the artwork.

## Troubleshoot Clef

### Restore Apple Music connectivity

Do the following:

1. Open Apple Music for Windows.
2. Play a song for a few seconds.
3. Wait for the touch strip to reconnect.

Apple Music for Windows doesn't always create its media and audio sessions
until it plays audio.

### Restore volume connectivity

Play a song once so that Windows creates the Apple Music for Windows audio
session. Clef searches every active output device and reconnects automatically
when `Amp Library Agent` changes.

### Wait for album artwork

Keep playback running for a few seconds. Windows can publish the new title
before it publishes the new thumbnail. Clef shows its neutral music placeholder
instead of pairing the previous track's cover with the new title.

### Previous-control behavior

Apple Music for Windows can treat the first Previous request as a restart. Clef
checks whether the track changed and sends one additional direct transport
request when necessary.

If the issue continues, [open a GitHub issue](https://github.com/Soldrynn/StreamDeck-Clef/issues/new)
and include the Windows, Stream Deck, Apple Music for Windows, and Clef versions.

## Known limitations

- Clef supports only Windows 11 x64 and Stream Deck +.
- Apple Music for Windows does not provide reliable public seeking through its
  Windows media session, so Clef does not provide seek controls.
- Windows controls when album artwork becomes available.
- The artwork-refresh fallback can cause a brief playback interruption after a
  dial skip.
- Clef does not use Apple logos, Apple artwork, keyboard automation, mouse
  automation, or Stream Deck multi-actions.

## Build from source

### Development requirements

To build Clef, you need the following development environment:

- Windows 11 on an x64 computer.
- Node.js 24 or later, including npm.
- .NET 8 SDK for x64. The .NET runtime alone is not sufficient.

### Configure .NET SDK discovery

The helper build uses the first available .NET executable in the following
order:

1. The path in the `CLEF_DOTNET` environment variable.
2. The project-local `.tools\dotnet.exe` file.
3. The `dotnet` command on `PATH`.

To use an SDK in another directory for the current PowerShell session, set
`CLEF_DOTNET` before you build:

```powershell
$env:CLEF_DOTNET = "C:\path\to\dotnet.exe"
```

For a project-local, non-administrator installation, use Microsoft's
[.NET install script](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script):

```powershell
New-Item -ItemType Directory -Force .tools | Out-Null
Invoke-WebRequest -UseBasicParsing `
  -Uri "https://dot.net/v1/dotnet-install.ps1" `
  -OutFile ".tools\dotnet-install.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File ".tools\dotnet-install.ps1" `
  -Channel 8.0 `
  -InstallDir ".tools" `
  -NoPath
```

The `.tools` directory is ignored by Git.

### Build and verify Clef

From the repository root, run the following commands in order:

```powershell
npm ci
npm run build
npm run check
npm run validate
npm run pack
```

Run `npm run build` before `npm run pack`. The pack command packages the
existing plugin files; it does not build a missing helper executable.

The build creates a self-contained Windows helper. The release check fails if
`helper\ClefBridge.exe` is missing from the plugin directory. The final installer
is written to `dist\com.davedev.clef.streamDeckPlugin`.

## Project layout

The repository uses the following top-level directories:

| Path | Purpose |
| --- | --- |
| `branding/` | Source artwork for the public Clef app icon. |
| `com.davedev.clef.sdPlugin/` | Manifest, layouts, property inspector, generated helper, and visual assets. |
| `docs/marketplace-media/` | Splash and store images. |
| `helper/ClefBridge/` | C# Windows media-session and Core Audio helper source. |
| `scripts/` | Build, validation, packaging, smoke-test, and memory-test commands. |
| `src/` | TypeScript Stream Deck actions, rendering, settings, and helper supervision. |
| `tests/` | TypeScript behavior and regression tests. |

## Runtime architecture

The plugin and helper communicate through private local standard-input and
standard-output pipes. Clef uses an event-driven design and direct Windows APIs.
Clef does not send telemetry.

## Contribute

Use [GitHub issues](https://github.com/Soldrynn/StreamDeck-Clef/issues) for
reproducible bugs and focused feature requests. Pull requests should preserve
the Windows-only scope, direct API design, bounded memory behavior, and neutral
visual system.

## License

This project is licensed under the [Apache License 2.0](LICENSE). Bundled
third-party components remain under their original licenses; see
[third-party notices](THIRD_PARTY_NOTICES.md).

Apple Music is a trademark of Apple Inc. Stream Deck and Elgato are trademarks
of Corsair Memory, Inc. This independent project is not affiliated with or
endorsed by Apple or Elgato.
