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

    [HttpGet("Status")]
    public async Task<ActionResult<PlaylistStatus>> GetStatus(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var status = await _service.GetStatusAsync(userId, cancellationToken).ConfigureAwait(false);
        return Ok(status);
    }

    [HttpPost("Items")]
    public async Task<ActionResult<PlaylistStatus>> AddItems(
        [FromBody] AddItemsRequest? request,
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

        await _service.AddItemsAsync(userId, request.ItemIds, cancellationToken).ConfigureAwait(false);
        var status = await _service.GetStatusAsync(userId, cancellationToken).ConfigureAwait(false);
        return Ok(status);
    }

    [HttpDelete("Items/{itemId:guid}")]
    public async Task<ActionResult<PlaylistStatus>> RemoveItem(Guid itemId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        await _service.RemoveItemAsync(userId, itemId, cancellationToken).ConfigureAwait(false);
        var status = await _service.GetStatusAsync(userId, cancellationToken).ConfigureAwait(false);
        return Ok(status);
    }

    [HttpPost("Reset")]
    public async Task<ActionResult<PlaylistStatus>> Reset(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        await _service.ResetAsync(userId, cancellationToken).ConfigureAwait(false);
        var status = await _service.GetStatusAsync(userId, cancellationToken).ConfigureAwait(false);
        return Ok(status);
    }

    [HttpPost("Cleanup")]
    public async Task<ActionResult<PlaylistStatus>> Cleanup(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var status = await _service.CleanupAsync(userId, cancellationToken).ConfigureAwait(false);
        return Ok(status);
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
