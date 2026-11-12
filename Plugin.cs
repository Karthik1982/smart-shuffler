using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SmartShufflePlaylist;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PlaylistName = "SmartShufflePlaylist";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Smart Shuffle Playlist";

    public override Guid Id => Guid.Parse("7a28699c-e118-4666-84a0-e106de3866fa");

    public override string Description =>
        "Only plays unwatched videos from a dedicated playlist, tracks progress with configurable thresholds, and resets the cycle once everything has been played.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "SmartShufflePlaylist",
            DisplayName = "Smart Shuffle Playlist",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            EnableInMainMenu = true,
            MenuIcon = "playlist_play"
        };
    }
}
