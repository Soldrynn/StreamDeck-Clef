# Apple Music for Stream Deck +

![Apple Music for Stream Deck + icon](com.davedev.apple-music.sdPlugin/assets/plugin-icon.png)

[![Windows 11](https://img.shields.io/badge/Windows-11-555555)](https://www.microsoft.com/windows/windows-11)
[![Stream Deck +](https://img.shields.io/badge/Stream%20Deck-%2B-555555)](https://www.elgato.com/us/en/p/stream-deck-plus-black)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-555555)](LICENSE)

Control Apple Music for Windows directly from the dials and touch strip on your Elgato Stream Deck +. Skip tracks, play or pause, change only Apple Music's volume, mute the app, and view live track information without keyboard shortcuts or simulated clicks.

## Features

The plugin provides the following features:

- Native playback controls through Windows media-session APIs.
- Per-app volume and mute controls that do not change your system volume.
- Automatic recovery when Apple Music, Stream Deck, or Windows restarts.
- Resilient volume binding when Windows renames `Amp Library Agent`, including numbered variants.
- Live title, artist, progress, volume, mute, and connection feedback.
- Album artwork with protection against delayed covers appearing on the wrong song.
- Independent settings for every Volume Controls action.
- A transparent, greyscale liquid-glass design made for the Stream Deck + touch strip.

## Requirements

Before you install the plugin, make sure that you have the following software and hardware:

- Windows 11 x64.
- Elgato Stream Deck 7.1 or later.
- Elgato Stream Deck +.
- Apple Music for Windows.

The release does not include an ARM64 helper.

## Install the plugin

To install the plugin:

1. Download `com.davedev.apple-music.streamDeckPlugin` from the [latest GitHub release](https://github.com/Soldrynn/StreamDeck-WindowsAppleMusic/releases/latest).
2. Double-click the downloaded file.
3. Approve the installation in Stream Deck.
4. Open the **Apple Music** category in the Stream Deck action list.
5. Drag **Playback Controls** or **Volume Controls** onto a Stream Deck + dial.

The plugin starts its local Windows helper automatically. You don't need to install .NET or configure an audio-session name.

## Use the controls

The two actions use the same interaction pattern:

| Action | Rotate left | Rotate right | Press or touch |
| --- | --- | --- | --- |
| Playback Controls | Previous track | Next track | Play or pause |
| Volume Controls | Lower Apple Music volume | Raise Apple Music volume | Mute or unmute Apple Music |

To change the volume step, select **Volume Controls** in Stream Deck and choose a value from 1% to 10%. The default step is 2%.

## Understand the touch-strip status

The touch strip reports the current state:

- Playback Controls shows the song title, artist, playback status, elapsed time, progress, and available album art.
- Volume Controls shows Apple Music's current volume and mute state.
- **Unavailable** means that Windows cannot currently find the matching media or audio session.
- **Reconnecting** means that the helper is recovering after an app, device, or Windows state change.

Apple Music can delay album artwork after a track change. If a dial-initiated skip still has no valid cover after 1.25 seconds, the plugin performs one brief pause-and-resume refresh and restores playback automatically.

## Troubleshoot the plugin

### Apple Music is unavailable

Do the following:

1. Open Apple Music for Windows.
2. Play a song for a few seconds.
3. Wait for the touch strip to reconnect.

Apple Music does not always create its media and audio sessions until it has played audio once.

### Volume is unavailable

Start playback once so that Windows creates the Apple Music audio session. The plugin searches every active output device and reconnects automatically when `Amp Library Agent` changes.

### Album artwork is delayed

Keep playback running for a few seconds. Windows can publish the new title before it publishes the new thumbnail. The plugin shows its neutral music placeholder instead of pairing a previous song's cover with the new title.

### Previous restarts the current song

Apple Music can treat the first Previous request as a restart. The plugin checks whether the song changed and sends one additional direct transport request when needed.

If the issue continues, [open a GitHub issue](https://github.com/Soldrynn/StreamDeck-WindowsAppleMusic/issues/new) and include your Windows, Stream Deck, Apple Music, and plugin versions.

## Known limitations

- The plugin supports Windows 11 x64 and Stream Deck + only.
- Apple Music for Windows does not provide reliable public seeking through its Windows media session, so the plugin does not offer seek controls.
- Windows controls when album artwork becomes available.
- The artwork-refresh fallback can cause a brief playback interruption after a dial skip.
- The plugin does not use Apple logos, Apple artwork, keyboard automation, mouse automation, or Stream Deck multi-actions.

## Build from source

You need the following development tools:

- Node.js 24 or later.
- .NET 8 SDK.

To build and verify the plugin:

```powershell
npm ci
npm run build
npm run check
npm run validate
npm run pack
```

The final installer is written to `dist/com.davedev.apple-music.streamDeckPlugin`. The Windows helper is self-contained, so plugin users don't need the .NET runtime.

## Project layout

The repository uses the following top-level directories:

| Path | Purpose |
| --- | --- |
| `src/` | TypeScript Stream Deck actions, rendering, settings, and helper supervision. |
| `helper/AppleMusicBridge/` | C# Windows media-session and Core Audio helper. |
| `com.davedev.apple-music.sdPlugin/` | Stream Deck manifest, layouts, property inspector, and visual assets. |
| `tests/` | TypeScript behavior and regression tests. |
| `scripts/` | Build, validation, packaging, smoke-test, and memory-test commands. |

The plugin and helper communicate through private local standard-input and standard-output pipes. Runtime control is event-driven, uses direct Windows APIs, and does not send telemetry.

## Contribute

Use [GitHub issues](https://github.com/Soldrynn/StreamDeck-WindowsAppleMusic/issues) for reproducible bugs and focused feature requests. Pull requests should keep the Windows-only scope, direct API design, bounded memory behavior, and neutral visual system intact.

## License

This project is licensed under the [Apache License 2.0](LICENSE). Bundled third-party components remain under their original licenses; see [third-party notices](THIRD_PARTY_NOTICES.md).

Apple Music is a trademark of Apple Inc. Stream Deck and Elgato are trademarks of Corsair Memory, Inc. This independent project is not affiliated with or endorsed by Apple or Elgato.
