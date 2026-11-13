using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartShufflePlaylist.Models;
using Jellyfin.Plugin.SmartShufflePlaylist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartShufflePlaylist.Api;

[ApiController]
[Authorize]
[Route("SmartShufflePlaylist")]
public sealed class SmartShuffleController : ControllerBase
{
    private readonly SmartShuffleService _service;

    public SmartShuffleController(SmartShuffleService service)
    {
        _service = service;
    }

    [HttpGet("Playlists")]
    public async Task<ActionResult<PlaylistsOverview>> GetPlaylists(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        return Ok(await _service.GetPlaylistsAsync(userId, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("Playlists")]
    public async Task<ActionResult<PlaylistsOverview>> CreatePlaylist(
        [FromBody] CreatePlaylistRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest("Playlist name is required.");
        }

        return Ok(await _service.CreateManagedPlaylistAsync(userId, request.Name, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("ActivePlaylist")]
    public async Task<ActionResult<PlaylistsOverview>> SetActivePlaylist(
        [FromBody] SetActivePlaylistRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (request?.PlaylistId == null || request.PlaylistId == Guid.Empty)
        {
            return BadRequest("Playlist id is required.");
        }

        return Ok(await _service.SetActivePlaylistAsync(userId, request.PlaylistId.Value, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("Status")]
    public async Task<ActionResult<PlaylistStatus>> GetStatus([FromQuery] Guid? playlistId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _service.GetStatusAsync(userId, playlistId, cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidOperationException)
        {
            if (playlistId.HasValue)
            {
                return NotFound();
            }

            throw;
        }
    }

    [HttpPost("Items")]
    public async Task<ActionResult<PlaylistStatus>> AddItems(
        [FromBody] AddItemsRequest? request,
        [FromQuery] Guid? playlistId,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (request?.ItemIds == null || request.ItemIds.Count == 0)
        {
            return BadRequest("No items were supplied.");
        }

        await _service.AddItemsAsync(userId, playlistId, request.ItemIds, cancellationToken).ConfigureAwait(false);
        return Ok(await _service.GetStatusAsync(userId, playlistId, cancellationToken).ConfigureAwait(false));
    }

    [HttpDelete("Items/{itemId:guid}")]
    public async Task<ActionResult<PlaylistStatus>> RemoveItem(Guid itemId, [FromQuery] Guid? playlistId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        await _service.RemoveItemAsync(userId, playlistId, itemId, cancellationToken).ConfigureAwait(false);
        return Ok(await _service.GetStatusAsync(userId, playlistId, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("Reset")]
    public async Task<ActionResult<PlaylistStatus>> Reset([FromQuery] Guid? playlistId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        await _service.ResetAsync(userId, playlistId, cancellationToken).ConfigureAwait(false);
        return Ok(await _service.GetStatusAsync(userId, playlistId, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("Cleanup")]
    public async Task<ActionResult<PlaylistStatus>> Cleanup([FromQuery] Guid? playlistId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        return Ok(await _service.CleanupAsync(userId, playlistId, cancellationToken).ConfigureAwait(false));
    }

    private Guid GetUserId()
    {
        var value = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(value, out var userId) ? userId : Guid.Empty;
    }
}

public sealed class AddItemsRequest
{
    public List<Guid> ItemIds { get; set; } = new();
}

public sealed class CreatePlaylistRequest
{
    public string Name { get; set; } = string.Empty;
}

public sealed class SetActivePlaylistRequest
{
    public Guid? PlaylistId { get; set; }
}
