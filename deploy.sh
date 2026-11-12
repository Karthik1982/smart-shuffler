#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

dotnet build -c Release

plugin_dir="$HOME/jellyfin-dev/data/plugins/SmartShufflePlaylist_1.0.0.0"
mkdir -p "$plugin_dir"
cp "bin/Release/net10.0/Jellyfin.Plugin.SmartShufflePlaylist.dll" "$plugin_dir/"
cp "meta.json" "$plugin_dir/"

echo "Deployed to $plugin_dir"
