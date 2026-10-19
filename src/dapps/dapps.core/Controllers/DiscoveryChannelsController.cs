using dapps.client.Discovery;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// REST surface for the discovery-channel table - the per-bearer,
/// per-physical-port configuration that drives the discovery daemon.
/// Parallel in shape to <c>/Neighbours</c>: list, upsert, delete.
///
/// Identity is (bearer, channelKey). POST is upsert (re-POSTing a
/// channel changes its tunables without erroring). Channel defaults
/// (cadence, ttl, cost) are derived from <c>LinkClass</c> when fields
/// are left at their zero values.
/// </summary>
[ApiController]
[Route("[controller]")]
public class DiscoveryChannelsController(Database database, DiscoveryService discovery) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<DiscoveryChannelModel>> List()
    {
        var rows = await database.GetDiscoveryChannels();
        return rows.Select(c => new DiscoveryChannelModel(
            Id: c.Id,
            Bearer: c.Bearer,
            ChannelKey: c.ChannelKey,
            LinkClass: c.LinkClass.ToString(),
            BeaconIntervalSeconds: c.BeaconIntervalSeconds,
            AdvertisedTtlSeconds: c.AdvertisedTtlSeconds,
            CostHint: c.CostHint,
            Enabled: c.Enabled,
            Notes: c.Notes,
            AirtimeBudgetSecondsPerHour: c.AirtimeBudgetSecondsPerHour,
            SolicitIntervalSeconds: c.SolicitIntervalSeconds,
            PortPending: c.PortPending == true));
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] DiscoveryChannelModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Bearer)) return BadRequest("Bearer is required");
        if (string.IsNullOrWhiteSpace(model.ChannelKey)) return BadRequest("ChannelKey is required");
        if (!Enum.TryParse<LinkClass>(model.LinkClass, ignoreCase: true, out var linkClass))
        {
            return BadRequest($"LinkClass must be one of: {string.Join(", ", Enum.GetNames<LinkClass>())}");
        }
        var bearer = model.Bearer.Trim().ToLowerInvariant();
        if (bearer == "agw"
            && (!int.TryParse(model.ChannelKey.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                || number is < 1 or > 255))
        {
            return BadRequest("ChannelKey: for an AGW channel, the node's own number for the radio port (XRouter's PORT=, BPQ's PORTNUM); ports count from 1.");
        }

        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = bearer,
            ChannelKey = model.ChannelKey.Trim(),
            LinkClass = linkClass,
            BeaconIntervalSeconds = model.BeaconIntervalSeconds,
            AdvertisedTtlSeconds = model.AdvertisedTtlSeconds,
            CostHint = model.CostHint,
            Enabled = model.Enabled,
            Notes = model.Notes ?? "",
            AirtimeBudgetSecondsPerHour = model.AirtimeBudgetSecondsPerHour,
            SolicitIntervalSeconds = model.SolicitIntervalSeconds,
        });
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(int id)
    {
        var removed = await database.RemoveDiscoveryChannel(id);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>
    /// Plan B6.2 - fire a one-shot solicit on the named channel.
    /// Receivers reply with their normal beacon after a small random
    /// delay; replies arrive on the standard beacon path and populate
    /// <c>DbDiscoveredPeer</c>. Useful for ad-hoc "anyone there?"
    /// inspection from the dashboard, especially on HF where scheduled
    /// beacons may have missed a propagation window.
    /// </summary>
    [HttpPost("{id}/solicit")]
    public async Task<IActionResult> Solicit(int id, CancellationToken ct)
    {
        var channels = await database.GetDiscoveryChannels();
        var channel = channels.FirstOrDefault(c => c.Id == id);
        if (channel is null) return NotFound();
        if (!channel.Enabled)
        {
            return BadRequest($"Channel {id} ({channel.Bearer}/{channel.ChannelKey}) is disabled");
        }
        if (channel.PortPending == true)
        {
            return Conflict($"Channel {id}'s port was saved by an older DAPPS and is waiting for the node's AGW connection to answer, to be converted to the node's own port number; nothing is sent on it until then.");
        }

        try
        {
            await discovery.SolicitAsync(channel.Bearer, channel.ChannelKey, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            // Bearer not currently running - DiscoveryService either
            // didn't start (no enabled channels at boot) or the bearer
            // failed during init. Surface as 503 so a sysop knows the
            // problem is transient/state, not a bad request.
            return StatusCode(503, ex.Message);
        }
    }
}

/// <summary>
/// Wire shape for /DiscoveryChannels. Leave numeric fields at 0 to pick
/// up <see cref="LinkClassDefaults"/> for the chosen <c>LinkClass</c>.
/// For an AGW channel <see cref="ChannelKey"/> is the node's own number
/// for the radio port, counting from 1 (#233; up to 0.53 it counted from
/// 0). <see cref="PortPending"/> (read-only) is true while a channel saved
/// by an older DAPPS waits to be converted to that number.
/// </summary>
public sealed record DiscoveryChannelModel(
    int Id,
    string Bearer,
    string ChannelKey,
    string LinkClass,
    int BeaconIntervalSeconds = 0,
    int AdvertisedTtlSeconds = 0,
    int CostHint = 0,
    bool Enabled = true,
    string? Notes = null,
    int AirtimeBudgetSecondsPerHour = 0,
    int SolicitIntervalSeconds = 0,
    bool PortPending = false);
