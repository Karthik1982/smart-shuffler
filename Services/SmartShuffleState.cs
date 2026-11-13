using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartShufflePlaylist.Services;

public sealed class SmartShuffleState
{
    public Dictionary<string, UserPlaylistState> Users { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UserPlaylistState
{
    // Legacy fields for migration.
    public string? PlaylistId { get; set; }
    public HashSet<string>? PlayedItemIds { get; set; }

    public string? ActivePlaylistId { get; set; }

    public List<ManagedPlaylistState> Playlists { get; set; } = new();
}

public sealed class ManagedPlaylistState
{
    public string PlaylistId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public HashSet<string> PlayedItemIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
