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
    private readonly ILibraryManager _libraryManager;
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
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        ILogger<SmartShuffleService> logger)
    {
        _playlistManager = playlistManager;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _stateFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "SmartShufflePlaylist.state.json");
    }

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string DefaultPlaylistName()
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

    public async Task<PlaylistsOverview> GetPlaylistsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var userState = await EnsureUserStateAsync(state, userId, cancellationToken).ConfigureAwait(false);
        var playlists = new List<ManagedPlaylistInfo>();
        var changed = false;

        foreach (var managed in userState.Playlists.ToList())
        {
            if (!Guid.TryParse(managed.PlaylistId, out var playlistGuid))
            {
                userState.Playlists.Remove(managed);
                changed = true;
                continue;
            }

            var playlist = _playlistManager.GetPlaylistForUser(playlistGuid, userId);
            if (playlist == null)
            {
                userState.Playlists.Remove(managed);
                changed = true;
                continue;
            }

            var items = GetPlaylistItems(playlist);
            var playedSet = NormalizePlayedSet(managed);
            var played = items.Count(i => playedSet.Contains(ToKey(i.Id)));
            playlists.Add(new ManagedPlaylistInfo
            {
                PlaylistId = ToKey(playlist.Id),
                Name = playlist.Name ?? managed.Name,
                Total = items.Count,
                Played = played,
                Remaining = items.Count - played,
                IsActive = string.Equals(userState.ActivePlaylistId, ToKey(playlist.Id), StringComparison.OrdinalIgnoreCase)
            });
            if (!string.Equals(managed.Name, playlist.Name, StringComparison.Ordinal))
            {
                managed.Name = playlist.Name ?? managed.Name;
                changed = true;
            }
        }

        if (userState.ActivePlaylistId != null && !playlists.Any(p => p.IsActive))
        {
            userState.ActivePlaylistId = playlists.FirstOrDefault()?.PlaylistId;
            if (playlists.Count > 0) playlists[0].IsActive = true;
            changed = true;
        }

        if (changed)
        {
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return new PlaylistsOverview
        {
            ActivePlaylistId = userState.ActivePlaylistId,
            Playlists = playlists
        };
    }

    public async Task<PlaylistsOverview> CreateManagedPlaylistAsync(Guid userId, string name, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        var trimmed = string.IsNullOrWhiteSpace(name) ? DefaultPlaylistName() : name.Trim();

        var existingOverview = await GetPlaylistsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (existingOverview.Playlists.Any(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A playlist named '{trimmed}' already exists.");
        }

        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = trimmed,
                ItemIdList = Array.Empty<Guid>(),
                MediaType = MediaType.Video,
                UserId = userId,
                Public = Configuration.SharedPlaylist
            }).ConfigureAwait(false);

            if (!Guid.TryParse(result.Id, out var playlistId))
            {
                throw new InvalidOperationException("Jellyfin returned an invalid playlist identifier.");
            }

            await UpdateStateAsync(state =>
            {
                var userState = GetOrCreateUserState(state, userId);
                EnsureMigrated(userState);
                // re-check uniqueness inside lock
                if (userState.Playlists.Any(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"A playlist named '{trimmed}' already exists.");
                }
                if (!userState.Playlists.Any(p => string.Equals(p.PlaylistId, ToKey(playlistId), StringComparison.OrdinalIgnoreCase)))
                {
                    userState.Playlists.Add(new ManagedPlaylistState
                    {
                        PlaylistId = ToKey(playlistId),
                        Name = trimmed,
                        PlayedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    });
                }
                userState.ActivePlaylistId = ToKey(playlistId);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Created managed playlist {Playlist} for user {User}.", trimmed, userId);
        }
        finally
        {
            _ensureLock.Release();
        }

        return await GetPlaylistsAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaylistsOverview> DeleteManagedPlaylistAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        var key = ToKey(playlistId);

        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Guid.TryParse(key, out var guid))
            {
                var playlist = _playlistManager.GetPlaylistForUser(guid, userId);
                if (playlist != null)
                {
                    try
                    {
                        _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true }, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete Jellyfin playlist {PlaylistId} for user {User}.", guid, userId);
                        throw new InvalidOperationException("Could not delete the playlist: " + ex.Message);
                    }
                }
            }

            await UpdateStateAsync(state =>
            {
                var userState = GetOrCreateUserState(state, userId);
                EnsureMigrated(userState);
                var removed = userState.Playlists.RemoveAll(p => string.Equals(p.PlaylistId, key, StringComparison.OrdinalIgnoreCase)) > 0;
                if (string.Equals(userState.ActivePlaylistId, key, StringComparison.OrdinalIgnoreCase))
                {
                    userState.ActivePlaylistId = userState.Playlists.FirstOrDefault()?.PlaylistId;
                }
                return removed || userState.ActivePlaylistId == null;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ensureLock.Release();
        }

        var overview = await GetPlaylistsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (overview.Playlists.Count == 0)
        {
            overview = await CreateManagedPlaylistAsync(userId, DefaultPlaylistName(), cancellationToken).ConfigureAwait(false);
        }
        return overview;
    }

    public async Task<PlaylistsOverview> SetActivePlaylistAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        var key = ToKey(playlistId);
        await UpdateStateAsync(state =>
        {
            var userState = GetOrCreateUserState(state, userId);
            EnsureMigrated(userState);
            if (!userState.Playlists.Any(p => string.Equals(p.PlaylistId, key, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Playlist is not managed by Smart Shuffle.");
            }
            userState.ActivePlaylistId = key;
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return await GetPlaylistsAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaylistStatus> GetStatusAsync(Guid userId, Guid? playlistId = null, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        var (playlist, managed) = await ResolveManagedPlaylistAsync(userId, playlistId, cancellationToken).ConfigureAwait(false);
        playlist = await PreparePlaylistAsync(playlist, userId, cancellationToken, false).ConfigureAwait(false);
        var items = GetPlaylistItems(playlist);
        var playedSet = NormalizePlayedSet(managed);

        var statusItems = items.Select(item => new PlaylistStatusItem
        {
            Id = ToKey(item.Id),
            Name = item.Name,
            RunTimeTicks = item.RunTimeTicks,
            Played = playedSet.Contains(ToKey(item.Id))
        }).ToList();

        var played = statusItems.Count(i => i.Played);
        return new PlaylistStatus
        {
            PlaylistId = ToKey(playlist.Id),
            PlaylistName = playlist.Name ?? managed.Name,
            Total = statusItems.Count,
            Played = played,
            Remaining = statusItems.Count - played,
            Items = statusItems
        };
    }

    public async Task AddItemsAsync(Guid userId, Guid? playlistId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0) return;
        var (playlist, _) = await ResolveManagedPlaylistAsync(userId, playlistId, cancellationToken).ConfigureAwait(false);
        var existing = GetPlaylistItems(playlist).Select(item => item.Id).ToHashSet();
        var toAdd = itemIds.Where(id => id != Guid.Empty && !existing.Contains(id)).Distinct().ToList();
        if (toAdd.Count == 0) return;
        await _playlistManager.AddItemToPlaylistAsync(playlist.Id, toAdd, null, userId).ConfigureAwait(false);
        _logger.LogInformation("Added {Count} item(s) to {Playlist} for user {User}.", toAdd.Count, playlist.Name, userId);
    }

    public async Task RemoveItemAsync(Guid userId, Guid? playlistId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var (playlist, _) = await ResolveManagedPlaylistAsync(userId, playlistId, cancellationToken).ConfigureAwait(false);
        await _playlistManager.RemoveItemFromPlaylistAsync(ToKey(playlist.Id), new[] { ToKey(itemId) }).ConfigureAwait(false);
        _logger.LogInformation("Removed item {Item} from {Playlist} for user {User}.", itemId, playlist.Name, userId);
    }

    public async Task ResetAsync(Guid userId, Guid? playlistId, CancellationToken cancellationToken = default)
    {
        var (playlist, managed) = await ResolveManagedPlaylistAsync(userId, playlistId, cancellationToken).ConfigureAwait(false);
        await ResetPlaylistProgressAsync(userId, managed.PlaylistId, GetPlaylistItems(playlist), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Reset {Playlist} progress for user {User}.", playlist.Name, userId);
    }

    public async Task<PlaylistStatus> CleanupAsync(Guid userId, Guid? playlistId, CancellationToken cancellationToken = default)
    {
        var (playlist, _) = await ResolveManagedPlaylistAsync(userId, playlistId, cancellationToken).ConfigureAwait(false);
        await PreparePlaylistAsync(playlist, userId, cancellationToken, true).ConfigureAwait(false);
        return await GetStatusAsync(userId, playlistId, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureUser(Guid userId)
    {
        _ = _userManager.GetUserById(userId) ?? throw new InvalidOperationException("The user could not be loaded.");
    }

    private async Task<(Playlist Playlist, ManagedPlaylistState Managed)> ResolveManagedPlaylistAsync(Guid userId, Guid? playlistId, CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var userState = await EnsureUserStateAsync(state, userId, cancellationToken).ConfigureAwait(false);
        var key = playlistId.HasValue && playlistId.Value != Guid.Empty ? ToKey(playlistId.Value) : userState.ActivePlaylistId;
        if (string.IsNullOrEmpty(key))
        {
            if (userState.Playlists.Count == 0)
            {
                await CreateManagedPlaylistAsync(userId, DefaultPlaylistName(), cancellationToken).ConfigureAwait(false);
                state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
                userState = GetOrCreateUserState(state, userId);
                key = userState.ActivePlaylistId ?? userState.Playlists.FirstOrDefault()?.PlaylistId;
            }
            else
            {
                key = userState.Playlists[0].PlaylistId;
            }
        }

        var managed = userState.Playlists.FirstOrDefault(p => string.Equals(p.PlaylistId, key, StringComparison.OrdinalIgnoreCase));
        if (managed == null || !Guid.TryParse(managed.PlaylistId, out var guid))
        {
            throw new InvalidOperationException("The requested playlist was not found.");
        }

        var playlist = _playlistManager.GetPlaylistForUser(guid, userId);
        if (playlist == null)
        {
            await UpdateStateAsync(s =>
            {
                var us = GetOrCreateUserState(s, userId);
                us.Playlists.RemoveAll(p => string.Equals(p.PlaylistId, key, StringComparison.OrdinalIgnoreCase));
                if (string.Equals(us.ActivePlaylistId, key, StringComparison.OrdinalIgnoreCase)) us.ActivePlaylistId = us.Playlists.FirstOrDefault()?.PlaylistId;
                return true;
            }, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The playlist could not be loaded.");
        }

        if (!string.Equals(userState.ActivePlaylistId, managed.PlaylistId, StringComparison.OrdinalIgnoreCase))
        {
            userState.ActivePlaylistId = managed.PlaylistId;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return (playlist, managed);
    }

    private async Task<UserPlaylistState> EnsureUserStateAsync(SmartShuffleState state, Guid userId, CancellationToken cancellationToken)
    {
        var userState = GetOrCreateUserState(state, userId);
        var changed = EnsureMigrated(userState);

        if (userState.Playlists.Count == 0)
        {
            await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // try find by default name for shared or create
                Playlist? playlist = null;
                if (Configuration.SharedPlaylist)
                {
                    var name = DefaultPlaylistName();
                    playlist = _playlistManager.GetPlaylists(userId)
                        .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                }
                if (playlist == null)
                {
                    var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                    {
                        Name = DefaultPlaylistName(),
                        ItemIdList = Array.Empty<Guid>(),
                        MediaType = MediaType.Video,
                        UserId = userId,
                        Public = Configuration.SharedPlaylist
                    }).ConfigureAwait(false);
                    if (Guid.TryParse(result.Id, out var pid))
                    {
                        playlist = _playlistManager.GetPlaylistForUser(pid, userId);
                    }
                }
                if (playlist != null)
                {
                    userState.Playlists.Add(new ManagedPlaylistState
                    {
                        PlaylistId = ToKey(playlist.Id),
                        Name = playlist.Name ?? DefaultPlaylistName(),
                        PlayedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    });
                    userState.ActivePlaylistId = ToKey(playlist.Id);
                    changed = true;
                }
            }
            finally
            {
                _ensureLock.Release();
            }
        }

        if (userState.ActivePlaylistId == null && userState.Playlists.Count > 0)
        {
            userState.ActivePlaylistId = userState.Playlists[0].PlaylistId;
            changed = true;
        }

        if (changed) await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        return userState;
    }

    private static bool EnsureMigrated(UserPlaylistState userState)
    {
        var changed = false;
        userState.Playlists ??= new List<ManagedPlaylistState>();
        if (!string.IsNullOrEmpty(userState.PlaylistId) && !userState.Playlists.Any(p => string.Equals(p.PlaylistId, userState.PlaylistId, StringComparison.OrdinalIgnoreCase)))
        {
            userState.Playlists.Insert(0, new ManagedPlaylistState
            {
                PlaylistId = userState.PlaylistId!,
                Name = DefaultPlaylistName(),
                PlayedItemIds = userState.PlayedItemIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            });
            changed = true;
        }
        if (userState.ActivePlaylistId == null && userState.Playlists.Count > 0)
        {
            userState.ActivePlaylistId = userState.Playlists[0].PlaylistId;
            changed = true;
        }
        if (userState.PlaylistId != null) { userState.PlaylistId = null; changed = true; }
        if (userState.PlayedItemIds != null) { userState.PlayedItemIds = null; changed = true; }
        return changed;
    }

    private async Task<Playlist> PreparePlaylistAsync(Playlist playlist, Guid userId, CancellationToken cancellationToken, bool force)
    {
        if (!force && !Configuration.AutoDedupe) return playlist;
        if (playlist.OwnerUserId != Guid.Empty && playlist.OwnerUserId != userId) return playlist;
        if (!force && _lastDedupe.TryGetValue(playlist.Id, out var lastDedupe) && DateTime.UtcNow - lastDedupe < TimeSpan.FromSeconds(30)) return playlist;

        var rawIds = GetRawPlaylistItemIds(playlist);
        var deduped = new List<Guid>(rawIds.Count);
        var seen = new HashSet<Guid>();
        foreach (var id in rawIds) if (seen.Add(id)) deduped.Add(id);
        if (deduped.Count == rawIds.Count) return playlist;

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
            _logger.LogInformation("Removed {Count} duplicate entries from {Playlist} for user {User}.", rawIds.Count - deduped.Count, playlist.Name, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean duplicate entries from {Playlist}.", playlist.Name);
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
            if (item != null) ids.Add(item.Id);
        }
        return ids;
    }

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) { try { await HandlePlaybackStartAsync(e).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "Failed to handle playback start for Smart Shuffle Playlist."); } }
    private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) { try { await HandleProgressAsync(e, false).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "Failed to handle playback progress for Smart Shuffle Playlist."); } }
    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) { try { await HandleProgressAsync(e, e.PlayedToCompletion).ConfigureAwait(false); } catch (Exception ex) { _logger.LogError(ex, "Failed to handle playback stop for Smart Shuffle Playlist."); } }

    private async Task HandlePlaybackStartAsync(PlaybackProgressEventArgs e)
    {
        if (e.Item == null || e.Session == null || e.Users == null) return;
        if (Configuration.SkipOnlyDuringPlaylistPlayback && string.IsNullOrEmpty(e.Session.PlaylistItemId)) return;

        foreach (var user in e.Users)
        {
            var managedList = await GetManagedPlaylistsForUserAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
            foreach (var (playlist, managed) in managedList)
            {
                var items = GetPlaylistItems(playlist);
                if (items.Count == 0 || items.All(item => item.Id != e.Item.Id)) continue;

                var playedSet = await GetPlayedSetAsync(user.Id, managed.PlaylistId, CancellationToken.None).ConfigureAwait(false);
                var playedCount = items.Count(item => playedSet.Contains(ToKey(item.Id)));
                if (playedCount >= items.Count)
                {
                    if (Configuration.AutoResetOnComplete)
                    {
                        await ResetPlaylistProgressAsync(user.Id, managed.PlaylistId, items, CancellationToken.None).ConfigureAwait(false);
                        playedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _logger.LogInformation("All items in {Playlist} played for user {User}; resetting.", playlist.Name, user.Id);
                        continue;
                    }
                }

                if (playedSet.Contains(ToKey(e.Item.Id)))
                {
                    await SkipToNextAsync(e.Session, user.Id, e.Item).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleProgressAsync(PlaybackProgressEventArgs e, bool playedToCompletion)
    {
        if (e.Item == null || e.Users == null) return;
        var runtimeTicks = e.Item.RunTimeTicks ?? 0;
        var positionTicks = e.PlaybackPositionTicks ?? 0;
        if (!playedToCompletion && (runtimeTicks <= 0 || positionTicks < runtimeTicks * GetWatchThreshold())) return;

        foreach (var user in e.Users)
        {
            var managedList = await GetManagedPlaylistsForUserAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
            foreach (var (playlist, managed) in managedList)
            {
                if (GetPlaylistItems(playlist).All(item => item.Id != e.Item.Id)) continue;
                var marked = await MarkItemPlayedAsync(user.Id, managed.PlaylistId, e.Item.Id, CancellationToken.None).ConfigureAwait(false);
                if (marked)
                {
                    _logger.LogInformation("Marked {Item} as played for user {User} in {Playlist}.", e.Item.Name, user.Id, playlist.Name);
                }
            }
        }
    }

    private async Task<List<(Playlist Playlist, ManagedPlaylistState Managed)>> GetManagedPlaylistsForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var list = new List<(Playlist, ManagedPlaylistState)>();
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var userState = await EnsureUserStateAsync(state, userId, cancellationToken).ConfigureAwait(false);
        foreach (var managed in userState.Playlists.ToList())
        {
            if (!Guid.TryParse(managed.PlaylistId, out var guid)) continue;
            var playlist = _playlistManager.GetPlaylistForUser(guid, userId);
            if (playlist != null) list.Add((playlist, managed));
        }
        return list;
    }

    private async Task<HashSet<string>> GetPlayedSetAsync(Guid userId, string playlistId, CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var userState = GetOrCreateUserState(state, userId);
        EnsureMigrated(userState);
        var managed = userState.Playlists.FirstOrDefault(p => string.Equals(p.PlaylistId, playlistId, StringComparison.OrdinalIgnoreCase));
        return NormalizePlayedSet(managed);
    }

    private async Task SkipToNextAsync(SessionInfo session, Guid userId, BaseItem item)
    {
        var key = session.Id + ":" + ToKey(item.Id);
        var now = DateTime.UtcNow;
        if (_lastSkip.TryGetValue(key, out var last) && now - last < GetSkipCooldown()) return;
        _lastSkip[key] = now;
        try
        {
            await _sessionManager.SendPlaystateCommand(session.Id, session.Id, new PlaystateRequest
            {
                Command = PlaystateCommand.NextTrack,
                ControllingUserId = ToKey(userId)
            }, CancellationToken.None).ConfigureAwait(false);
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
            if (item == null || !seen.Add(item.Id)) continue;
            items.Add(new PlaylistItemInfo { Id = item.Id, Name = item.Name ?? "(untitled)", RunTimeTicks = item.RunTimeTicks, Item = item });
        }
        return items;
    }

    private Task<bool> MarkItemPlayedAsync(Guid userId, string playlistId, Guid itemId, CancellationToken cancellationToken)
    {
        var key = ToKey(itemId);
        return UpdateStateAsync(state =>
        {
            var userState = GetOrCreateUserState(state, userId);
            EnsureMigrated(userState);
            var managed = userState.Playlists.FirstOrDefault(p => string.Equals(p.PlaylistId, playlistId, StringComparison.OrdinalIgnoreCase));
            if (managed == null) return false;
            managed.PlayedItemIds = NormalizePlayedSet(managed);
            return managed.PlayedItemIds.Add(key);
        }, cancellationToken);
    }

    private Task ResetPlaylistProgressAsync(Guid userId, string playlistId, IEnumerable<PlaylistItemInfo> items, CancellationToken cancellationToken)
    {
        var itemKeys = items.Select(item => ToKey(item.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return UpdateStateAsync(state =>
        {
            var userState = GetOrCreateUserState(state, userId);
            EnsureMigrated(userState);
            var managed = userState.Playlists.FirstOrDefault(p => string.Equals(p.PlaylistId, playlistId, StringComparison.OrdinalIgnoreCase));
            if (managed == null) return false;
            managed.PlayedItemIds = NormalizePlayedSet(managed);
            if (managed.PlayedItemIds.Count == 0) return false;
            var before = managed.PlayedItemIds.Count;
            if (itemKeys.Count == 0) managed.PlayedItemIds.Clear(); else managed.PlayedItemIds.RemoveWhere(itemKeys.Contains);
            return managed.PlayedItemIds.Count != before;
        }, cancellationToken);
    }

    private async Task<SmartShuffleState> LoadStateAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _stateLock.Release(); }
    }

    private async Task<bool> UpdateStateAsync(Func<SmartShuffleState, bool> update, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!update(state)) return false;
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _stateLock.Release(); }
    }

    private async Task SaveStateAsync(SmartShuffleState state, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false); }
        finally { _stateLock.Release(); }
    }

    private async Task<SmartShuffleState> LoadStateCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return new SmartShuffleState();
            await using var stream = File.OpenRead(_stateFilePath);
            var state = await JsonSerializer.DeserializeAsync<SmartShuffleState>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _stateFilePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _stateFilePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save Smart Shuffle Playlist state.");
        }
    }

    private static UserPlaylistState GetOrCreateUserState(SmartShuffleState state, Guid userId)
    {
        var key = ToKey(userId);
        if (!state.Users.TryGetValue(key, out var userState))
        {
            userState = new UserPlaylistState();
            state.Users[key] = userState;
        }
        return userState;
    }

    private static HashSet<string> NormalizePlayedSet(ManagedPlaylistState? managed)
    {
        if (managed?.PlayedItemIds == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (managed.PlayedItemIds.Comparer == StringComparer.OrdinalIgnoreCase) return managed.PlayedItemIds;
        managed.PlayedItemIds = new HashSet<string>(managed.PlayedItemIds, StringComparer.OrdinalIgnoreCase);
        return managed.PlayedItemIds;
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
