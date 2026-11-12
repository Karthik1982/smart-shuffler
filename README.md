# Smart Shuffle Playlist

Shuffle only what you haven’t watched—then start fresh when the cycle’s complete.

A Jellyfin plugin that plays unwatched videos from a dedicated playlist, tracks progress, and resets the cycle when everything has been played.

## Features

- Per-user smart shuffle playlist (private or shared)
- Configurable watch threshold and skip cooldown
- Playlist-only skipping with optional auto-reset
- Automatic duplicate cleanup and manual cleanup
- Progress on the plugin configuration page
- Native playlist detail progress (donut, percentage, played/remaining) when JavaScript Injector is installed

## Requirements

- Jellyfin Server **12.4.0+** (`targetAbi` 12.4.0.0)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source

### Optional dependency

- **[Jellyfin JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector)** — required to show progress on Jellyfin’s native playlist details page (`#/details?id=...`).  
  This plugin auto-registers its progress script with JavaScript Injector when both are installed and enabled. Without it, progress is still available on the plugin page (**Dashboard → Plugins → Smart Shuffle Playlist** or `/web/ConfigurationPage?name=SmartShufflePlaylist`).

## Install

### 1. Build

```bash
git clone https://github.com/Karthik1982/smart-shuffler.git
cd smart-shuffler
dotnet build -c Release
```

### 2. Copy plugin files

Copy these two files into a `SmartShufflePlaylist_1.0.0.0` folder inside your Jellyfin **plugins** directory:

- `bin/Release/net10.0/Jellyfin.Plugin.SmartShufflePlaylist.dll`
- `meta.json`

**Common plugin locations**

| Install type | Plugins directory |
| --- | --- |
| Debian/Ubuntu package (systemd) | `/var/lib/jellyfin/plugins` |
| Docker (official image) | `/config/plugins` (inside the container / your mapped config volume) |
| Windows (installer) | `%LOCALAPPDATA%\jellyfin\plugins` |

For a standard system install on Linux (e.g. this system: `JELLYFIN_DATA_DIR=/var/lib/jellyfin`), the target is:

```bash
sudo mkdir -p /var/lib/jellyfin/plugins/SmartShufflePlaylist_1.0.0.0
sudo cp bin/Release/net10.0/Jellyfin.Plugin.SmartShufflePlaylist.dll meta.json /var/lib/jellyfin/plugins/SmartShufflePlaylist_1.0.0.0/
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/SmartShufflePlaylist_1.0.0.0
sudo systemctl restart jellyfin
```

### Using `deploy.sh`

`deploy.sh` builds and copies the files for you. It accepts the plugins base directory as an argument:

```bash
# Dev instance (default: ~/jellyfin-dev/data/plugins)
./deploy.sh

# Production system install
sudo ./deploy.sh /var/lib/jellyfin/plugins
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/SmartShufflePlaylist_1.0.0.0
sudo systemctl restart jellyfin
```

Restart Jellyfin after installing or updating the plugin.

## Configuration

Open **Dashboard → Plugins → Smart Shuffle Playlist** (admin) to configure:

- Playlist name and shared/private playlist
- Watch threshold and skip cooldown
- Auto-reset, skip-only-during-playlist-playback, and auto-dedupe
- Add/remove items and run duplicate cleanup

## License

[MIT](LICENSE)
