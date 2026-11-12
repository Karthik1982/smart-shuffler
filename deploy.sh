#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

# Usage: ./deploy.sh [plugins_base_dir]
# Default: $HOME/jellyfin-dev/data/plugins (dev instance)
# Production (Debian/Ubuntu package): sudo ./deploy.sh /var/lib/jellyfin/plugins
plugins_base="${1:-$HOME/jellyfin-dev/data/plugins}"
plugin_dir="$plugins_base/SmartShufflePlaylist_1.0.0.0"

dotnet build -c Release

mkdir -p "$plugin_dir"
cp "bin/Release/net10.0/Jellyfin.Plugin.SmartShufflePlaylist.dll" "$plugin_dir/"
cp "meta.json" "$plugin_dir/"

echo "Deployed to $plugin_dir"
if [ "$(basename "$plugins_base")" = "plugins" ] && [ "$plugins_base" != "$HOME/jellyfin-dev/data/plugins" ]; then
  echo "Restart Jellyfin to load the plugin (e.g. sudo systemctl restart jellyfin)"
  echo "If needed, fix ownership: sudo chown -R jellyfin:jellyfin \"$plugin_dir\""
fi

