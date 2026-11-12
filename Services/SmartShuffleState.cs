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
    public string? PlaylistId { get; set; }

    public HashSet<string> PlayedItemIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
