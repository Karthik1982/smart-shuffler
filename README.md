# Smart Shuffle Playlist

Jellyfin plugin that plays unwatched videos from a dedicated playlist, tracks progress with configurable thresholds, and resets the cycle once everything has been played.

## Features

- Per-user smart shuffle playlist (private or shared, configurable)
- Configurable watch threshold and skip cooldown
- Playlist-only skipping with optional auto-reset when all items are played
- Automatic duplicate cleanup and manual cleanup
- Progress display on the plugin configuration page
- Native playlist detail progress (donut, percentage, and played/remaining summary) when JavaScript Injector is installed

## Requirements

- Jellyfin Server **12.4.0+** (`targetAbi` 12.4.0.0)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source

### Optional dependency

- **[Jellyfin JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector)** — required for showing progress on Jellyfin’s native playlist details page (`#/details?id=...`). This plugin auto-registers its progress script with JavaScript Injector when it is installed and enabled. Without it, progress is still available on the plugin page (**Dashboard → Plugins → Smart Shuffle Playlist** or `/web/ConfigurationPage?name=SmartShufflePlaylist`).

## Build & install

```bash
dotnet build -c Release
```

Or run `./deploy.sh` (builds and copies the DLL and `meta.json` to `~/jellyfin-dev/data/plugins/SmartShufflePlaylist_1.0.0.0/` by default; adjust the path for your server).

Manual install: copy `bin/Release/net10.0/Jellyfin.Plugin.SmartShufflePlaylist.dll` and `meta.json` into a `SmartShufflePlaylist_1.0.0.0` folder under your Jellyfin plugins directory, then restart Jellyfin.

## Configuration

Open **Dashboard → Plugins → Smart Shuffle Playlist** (admin) to configure:

- Playlist name and shared/private playlist
- Watch threshold and skip cooldown
- Auto-reset, skip-only-during-playlist-playback, and auto-dedupe
- Add/remove items and run duplicate cleanup

## License

[MIT](LICENSE)
