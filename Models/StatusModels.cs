using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartShufflePlaylist.Models;

public sealed class PlaylistStatus
{
    public string PlaylistId { get; set; } = string.Empty;

    public string PlaylistName { get; set; } = string.Empty;

    public int Total { get; set; }

    public int Played { get; set; }

    public int Remaining { get; set; }

    public List<PlaylistStatusItem> Items { get; set; } = new();
}

public sealed class PlaylistStatusItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Played { get; set; }

    public long? RunTimeTicks { get; set; }
}

public sealed class ManagedPlaylistInfo
{
    public string PlaylistId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Total { get; set; }

    public int Played { get; set; }

    public int Remaining { get; set; }

    public bool IsActive { get; set; }
}

public sealed class PlaylistsOverview
{
    public string? ActivePlaylistId { get; set; }

    public List<ManagedPlaylistInfo> Playlists { get; set; } = new();
}
