using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SmartShufflePlaylist;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public double WatchThresholdPercent { get; set; } = 95;

    public int SkipCooldownSeconds { get; set; } = 3;

    public string PlaylistName { get; set; } = "SmartShufflePlaylist";

    public bool SharedPlaylist { get; set; }

    public bool AutoResetOnComplete { get; set; } = true;

    public bool SkipOnlyDuringPlaylistPlayback { get; set; } = true;

    public bool AutoDedupe { get; set; } = true;
}
