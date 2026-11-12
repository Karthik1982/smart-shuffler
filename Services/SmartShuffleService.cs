using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartShufflePlaylist.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartShufflePlaylist.Services;

public sealed class SmartShuffleService : IHostedService, IDisposable
{
    private readonly IPlaylistManager _playlistManager;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<SmartShuffleService> _logger;
    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _lastSkip = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, DateTime> _lastDedupe = new();
    private bool _started;

    public SmartShuffleService(
        IPlaylistManager playlistManager,
        ISessionManager sessionManager,
        IUserManager userManager,
        IApplicationPaths applicationPaths,
        ILogger<SmartShuffleService> logger)
    {
        _playlistManager = playlistManager;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _logger = logger;
        _stateFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "SmartShufflePlaylist.state.json");
    }

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string GetPlaylistName()
    {
        var name = Configuration.PlaylistName;
        return string.IsNullOrWhiteSpace(name) ? "SmartShufflePlaylist" : name.Trim();
    }

    private static double GetWatchThreshold()
    {
        var percent = Configuration.WatchThresholdPercent;
        if (double.IsNaN(percent) || double.IsInfinity(percent))
        {
            percent = 95;
        }

        return Math.Clamp(percent / 100.0, 0.01, 1.0);
    }

    private static TimeSpan GetSkipCooldown()
    {
        return TimeSpan.FromSeconds(Math.Clamp(Configuration.SkipCooldownSeconds, 0, 300));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("Smart Shuffle Playlist playback monitor started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _started = false;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _stateLock.Dispose();
        _ensureLock.Dispose();
    }

    public async Task<PlaylistStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var playlist = await EnsurePlaylistAsync(userId, cancellationToken).ConfigureAwait(false);
        _ = _userManager.GetUserById(userId)
            ?? throw new InvalidOperationException("The user could not be loaded.");
        var items = GetPlaylistItems(playlist);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var userState = GetUserState(state, userId);
        var playedSet = userState.PlayedItemIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var statusItems = items
            .Select(item => new PlaylistStatusItem
            {
                Id = ToKey(item.Id),
                Name = item.Name,
                RunTimeTicks = item.RunTimeTicks,
                Played = playedSet.Contains(ToKey(item.Id))
            })
            .ToList();

        var played = statusItems.Count(item => item.Played);
        return new PlaylistStatus
        {
            PlaylistId = ToKey(playlist.Id),
            PlaylistName = playlist.Name ?? GetPlaylistName(),
            Total = statusItems.Count,
            Played = played,
            Remaining = statusItems.Count - played,
            Items = statusItems
        };
    }

    public async Task AddItemsAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        var playlist = await EnsurePlaylistAsync(userId, cancellationToken).ConfigureAwait(false);
        var existing = GetPlaylistItems(playlist).Select(item => item.Id).ToHashSet();
        var toAdd = itemIds
            .Where(id => id != Guid.Empty && !existing.Contains(id))
            .Distinct()
            .ToList();

        if (toAdd.Count == 0)
        {
            return;
        }

        await _playlistManager.AddItemToPlaylistAsync(playlist.Id, toAdd, null, userId).ConfigureAwait(false);
        _logger.LogInformation("Added {Count} item(s) to {Playlist} for user {User}.", toAdd.Count, GetPlaylistName(), userId);
    }

    public async Task RemoveItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var playlist = await EnsurePlaylistAsync(userId, cancellationToken).ConfigureAwait(false);
        await _playlistManager
            .RemoveItemFromPlaylistAsync(ToKey(playlist.Id), new[] { ToKey(itemId) })
            .ConfigureAwait(false);

        _logger.LogInformation("Removed item {Item} from {Playlist} for user {User}.", itemId, GetPlaylistName(), userId);
    }

    public async Task ResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var playlist = await EnsurePlaylistAsync(userId, cancellationToken).ConfigureAwait(false);
        _ = _userManager.GetUserById(userId)
            ?? throw new InvalidOperationException("The user could not be loaded.");

        await ResetPlaylistProgressAsync(userId, GetPlaylistItems(playlist), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Reset {Playlist} progress for user {User}.", GetPlaylistName(), userId);
    }

    public async Task<PlaylistStatus> CleanupAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var playlist = await EnsurePlaylistAsync(userId, cancellationToken).ConfigureAwait(false);
        await PreparePlaylistAsync(playlist, userId, cancellationToken, true).ConfigureAwait(false);
        return await GetStatusAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Playlist> EnsurePlaylistAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindPlaylistAsync(userId, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                await CachePlaylistIdAsync(userId, existing.Id, cancellationToken).ConfigureAwait(false);
                existing = await RenameIfNeededAsync(existing, userId, cancellationToken).ConfigureAwait(false);
                return await PreparePlaylistAsync(existing, userId, cancellationToken, false).ConfigureAwait(false);
            }

            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = GetPlaylistName(),
                ItemIdList = Array.Empty<Guid>(),
                MediaType = MediaType.Video,
                UserId = userId,
                Public = Configuration.SharedPlaylist
            }).ConfigureAwait(false);

            if (!Guid.TryParse(result.Id, out var playlistId))
            {
                throw new InvalidOperationException("Jellyfin returned an invalid playlist identifier.");
            }

            var playlist = _playlistManager.GetPlaylistForUser(playlistId, userId)
                ?? throw new InvalidOperationException("The newly created playlist could not be loaded.");

            await CachePlaylistIdAsync(userId, playlist.Id, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created playlist {Playlist} for user {User}.", GetPlaylistName(), userId);
            return await PreparePlaylistAsync(playlist, userId, cancellationToken, false).ConfigureAwait(false);
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private async Task<Playlist> RenameIfNeededAsync(
        Playlist playlist,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var desiredName = GetPlaylistName();
        if (string.Equals(playlist.Name, desiredName, StringComparison.Ordinal))
        {
            return playlist;
        }

        if (playlist.OwnerUserId != Guid.Empty && playlist.OwnerUserId != userId)
        {
            _logger.LogDebug("Skipping rename for {Playlist} because it is owned by another user.", playlist.Name);
            return playlist;
        }

        try
        {
            await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
            {
                Id = playlist.Id,
                UserId = userId,
                Name = desiredName,
                Ids = GetRawPlaylistItemIds(playlist),
                Public = playlist.OpenAccess
            }).ConfigureAwait(false);

            _logger.LogInformation("Renamed playlist {OldName} to {NewName} for user {User}.", playlist.Name, desiredName, userId);
            return _playlistManager.GetPlaylistForUser(playlist.Id, userId) ?? playlist;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not rename playlist {Playlist} to {NewName}.", playlist.Name, desiredName);
            return playlist;
        }
    }

    private async Task<Playlist> PreparePlaylistAsync(
        Playlist playlist,
        Guid userId,
        CancellationToken cancellationToken,
        bool force)
    {
        if (!force && !Configuration.AutoDedupe)
        {
            return playlist;
        }

        if (playlist.OwnerUserId != Guid.Empty && playlist.OwnerUserId != userId)
        {
            _logger.LogDebug(
                "Skipping duplicate cleanup for {Playlist} because it is owned by another user.",
                GetPlaylistName());
            return playlist;
        }

        if (!force
            && _lastDedupe.TryGetValue(playlist.Id, out var lastDedupe)
            && DateTime.UtcNow - lastDedupe < TimeSpan.FromSeconds(30))
        {
            return playlist;
        }

        var rawIds = GetRawPlaylistItemIds(playlist);
        var deduped = new List<Guid>(rawIds.Count);
        var seen = new HashSet<Guid>();

        foreach (var id in rawIds)
        {
            if (seen.Add(id))
            {
                deduped.Add(id);
            }
        }

        if (deduped.Count == rawIds.Count)
        {
            return playlist;
        }

        _logger.LogInformation(
            "Removing {Count} duplicate playlist entry/entries from {Playlist} for user {User}.",
            rawIds.Count - deduped.Count,
            GetPlaylistName(),
            userId);

        try
        {
            await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
            {
                Id = playlist.Id,
                UserId = userId,
                Name = playlist.Name,
                Ids = deduped,
                Public = playlist.OpenAccess
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean duplicate entries from {Playlist}.", GetPlaylistName());
            return playlist;
        }

        _lastDedupe[playlist.Id] = DateTime.UtcNow;
        return _playlistManager.GetPlaylistForUser(playlist.Id, userId) ?? playlist;
    }

    private static List<Guid> GetRawPlaylistItemIds(Playlist playlist)
    {
        var ids = new List<Guid>();
        foreach (var pair in playlist.GetManageableItems())
        {
            var item = pair.Item2;
            if (item != null)
            {
                ids.Add(item.Id);
            }
        }

        return ids;
    }

    private async Task<Playlist?> FindPlaylistAsync(Guid userId, CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var userState = GetUserState(state, userId);

        if (!string.IsNullOrEmpty(userState.PlaylistId) && Guid.TryParse(userState.PlaylistId, out var cachedId))
        {
            var cached = _playlistManager.GetPlaylistForUser(cachedId, userId);
            if (cached != null)
            {
                return cached;
            }
        }

        if (!Configuration.SharedPlaylist)
        {
            return null;
        }

        var name = GetPlaylistName();
        return _playlistManager
            .GetPlaylists(userId)
            .FirstOrDefault(playlist => string.Equals(playlist.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private Task CachePlaylistIdAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken)
    {
        return UpdateStateAsync(
            state =>
            {
                var userState = GetUserState(state, userId);
                var key = ToKey(playlistId);
                if (string.Equals(userState.PlaylistId, key, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                userState.PlaylistId = key;
                return true;
            },
            cancellationToken);
    }

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            await HandlePlaybackStartAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback start for Smart Shuffle Playlist.");
        }
    }

    private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            await HandleProgressAsync(e, false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback progress for Smart Shuffle Playlist.");
        }
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            await HandleProgressAsync(e, e.PlayedToCompletion).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback stop for Smart Shuffle Playlist.");
        }
    }

    private async Task HandlePlaybackStartAsync(PlaybackProgressEventArgs e)
    {
        if (e.Item == null || e.Session == null || e.Users == null)
        {
            return;
        }

        if (Configuration.SkipOnlyDuringPlaylistPlayback
            && string.IsNullOrEmpty(e.Session.PlaylistItemId))
        {
            return;
        }

        foreach (var user in e.Users)
        {
            var playlist = await FindPlaylistAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
            if (playlist == null)
            {
                continue;
            }

            var items = GetPlaylistItems(playlist);
            if (items.Count == 0 || items.All(item => item.Id != e.Item.Id))
            {
                continue;
            }

            var userEntity = _userManager.GetUserById(user.Id);
            if (userEntity == null)
            {
                continue;
            }

            var state = await LoadStateAsync(CancellationToken.None).ConfigureAwait(false);
            var userState = GetUserState(state, user.Id);
            var playedSet = userState.PlayedItemIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var playedCount = items.Count(item => playedSet.Contains(ToKey(item.Id)));

            if (playedCount >= items.Count)
            {
                if (!Configuration.AutoResetOnComplete)
                {
                    _logger.LogInformation(
                        "All items in {Playlist} have been played for user {User}, but auto-reset is disabled.",
                        GetPlaylistName(),
                        user.Id);
                }
                else
                {
                    await ResetPlaylistProgressAsync(user.Id, items, CancellationToken.None).ConfigureAwait(false);
                    state = await LoadStateAsync(CancellationToken.None).ConfigureAwait(false);
                    userState = GetUserState(state, user.Id);
                    playedSet = userState.PlayedItemIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _logger.LogInformation(
                        "All items in {Playlist} have been played for user {User}; resetting the cycle.",
                        GetPlaylistName(),
                        user.Id);
                    continue;
                }
            }

            if (playedSet.Contains(ToKey(e.Item.Id)))
            {
                await SkipToNextAsync(e.Session, user.Id, e.Item).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleProgressAsync(PlaybackProgressEventArgs e, bool playedToCompletion)
    {
        if (e.Item == null || e.Users == null)
        {
            return;
        }

        var runtimeTicks = e.Item.RunTimeTicks ?? 0;
        var positionTicks = e.PlaybackPositionTicks ?? 0;
        if (!playedToCompletion
            && (runtimeTicks <= 0 || positionTicks < runtimeTicks * GetWatchThreshold()))
        {
            return;
        }

        foreach (var user in e.Users)
        {
            var playlist = await FindPlaylistAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
            if (playlist == null)
            {
                continue;
            }

            if (GetPlaylistItems(playlist).All(item => item.Id != e.Item.Id))
            {
                continue;
            }

            var userEntity = _userManager.GetUserById(user.Id);
            if (userEntity == null)
            {
                continue;
            }

            var marked = await MarkItemPlayedAsync(user.Id, e.Item.Id, CancellationToken.None).ConfigureAwait(false);
            if (!marked)
            {
                continue;
            }

            _logger.LogInformation(
                "Marked {Item} as played for user {User} in {Playlist}.",
                e.Item.Name,
                user.Id,
                GetPlaylistName());
        }
    }

    private async Task SkipToNextAsync(SessionInfo session, Guid userId, BaseItem item)
    {
        var key = session.Id + ":" + ToKey(item.Id);
        var now = DateTime.UtcNow;
        if (_lastSkip.TryGetValue(key, out var last) && now - last < GetSkipCooldown())
        {
            return;
        }

        _lastSkip[key] = now;
        _logger.LogInformation(
            "Skipping already-played item {Item} for user {User} on session {Session}.",
            item.Name,
            userId,
            session.Id);

        try
        {
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest
                {
                    Command = PlaystateCommand.NextTrack,
                    ControllingUserId = ToKey(userId)
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send NextTrack to session {Session}.", session.Id);
        }
    }

    private static List<PlaylistItemInfo> GetPlaylistItems(Playlist playlist)
    {
        var items = new List<PlaylistItemInfo>();
        var seen = new HashSet<Guid>();

        foreach (var pair in playlist.GetManageableItems())
        {
            var item = pair.Item2;
            if (item == null || !seen.Add(item.Id))
            {
                continue;
            }

            items.Add(new PlaylistItemInfo
            {
                Id = item.Id,
                Name = item.Name ?? "(untitled)",
                RunTimeTicks = item.RunTimeTicks,
                Item = item
            });
        }

        return items;
    }

    private Task<bool> MarkItemPlayedAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var key = ToKey(itemId);
        return UpdateStateAsync(state =>
        {
            var userState = GetUserState(state, userId);
            userState.PlayedItemIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return userState.PlayedItemIds.Add(key);
        }, cancellationToken);
    }

    private Task ResetPlaylistProgressAsync(Guid userId, IEnumerable<PlaylistItemInfo> items, CancellationToken cancellationToken)
    {
        var itemKeys = items.Select(item => ToKey(item.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return UpdateStateAsync(state =>
        {
            var userState = GetUserState(state, userId);
            if (userState.PlayedItemIds == null || userState.PlayedItemIds.Count == 0)
            {
                return false;
            }

            var before = userState.PlayedItemIds.Count;
            if (itemKeys.Count == 0)
            {
                userState.PlayedItemIds.Clear();
            }
            else
            {
                userState.PlayedItemIds.RemoveWhere(itemKeys.Contains);
            }

            return userState.PlayedItemIds.Count != before;
        }, cancellationToken);
    }

    private async Task<SmartShuffleState> LoadStateAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<bool> UpdateStateAsync(
        Func<SmartShuffleState, bool> update,
        CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!update(state))
            {
                return false;
            }

            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<SmartShuffleState> LoadStateCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return new SmartShuffleState();
            }

            await using var stream = File.OpenRead(_stateFilePath);
            var state = await JsonSerializer
                .DeserializeAsync<SmartShuffleState>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return state ?? new SmartShuffleState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Smart Shuffle Playlist state; starting with a clean state.");
            return new SmartShuffleState();
        }
    }

    private async Task SaveStateCoreAsync(SmartShuffleState state, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _stateFilePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, state, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, _stateFilePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save Smart Shuffle Playlist state.");
        }
    }

    private static UserPlaylistState GetUserState(SmartShuffleState state, Guid userId)
    {
        var key = ToKey(userId);
        if (!state.Users.TryGetValue(key, out var userState))
        {
            userState = new UserPlaylistState();
            state.Users[key] = userState;
        }

        return userState;
    }

    private static string ToKey(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private sealed class PlaylistItemInfo
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public long? RunTimeTicks { get; init; }

        public BaseItem Item { get; init; } = null!;
    }
}
