using dapps.client;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// REST app interface - mirrors the MQTT topic structure for apps that
/// prefer plain HTTP. Same SQLite-backed queue, same ack contract.
///
/// Map (MQTT → REST):
///   `dapps/out/&lt;app&gt;/&lt;dest&gt;` → POST /AppApi/outbound
///   `dapps/in/&lt;app&gt;`           → GET  /AppApi/inbound/{app}
///   `dapps/ack/&lt;app&gt;`          → POST /AppApi/inbound/{app}/{id}/ack
/// </summary>
[ApiController]
[Route("[controller]")]
public class AppApiController(Database database) : ControllerBase
{
    /// <summary>
    /// Submit an outbound message for forwarding to a remote DAPPS instance.
    /// Equivalent to MQTT publish on `dapps/out/{App}/{DestCallsign}`.
    /// </summary>
    [HttpPost("outbound")]
    public async Task<ActionResult<OutboundResponse>> SubmitOutbound([FromBody] OutboundRequest request)
    {
        // App, callsign and stream id all go on the air in the offer
        // line, so each is checked against the one shared rule (#313).
        if (MessageFields.AppNameError(request.App) is { } appError) return BadRequest($"App: {appError}");
        if (MessageFields.CallsignError(request.DestCallsign) is { } callError) return BadRequest($"DestCallsign: {callError}");
        if (request.Payload is null || request.Payload.Length == 0) return BadRequest("Payload is required");
        if (request.Ttl is { } ttl && ttl <= 0) return BadRequest("Ttl must be a positive integer (seconds)");
        if (request.StreamId is { } sid && MessageFields.StreamIdError(sid) is { } sidError)
        {
            return BadRequest($"StreamId: {sidError}");
        }
        if (request.StreamId is not null && request.StreamGapTimeoutSeconds is null)
        {
            return BadRequest($"StreamGapTimeoutSeconds: {MessageFields.StreamGapTimeoutRequired}");
        }
        if (!HttpContext.IsAuthorisedForApp(request.App)) return Forbid();

        var id = await database.SubmitOutboundMessage(
            request.App, request.DestCallsign, request.Payload, request.Ttl,
            streamId: request.StreamId,
            streamGapTimeoutSeconds: request.StreamGapTimeoutSeconds);
        return Ok(new OutboundResponse(id));
    }

    /// <summary>
    /// List currently unacknowledged inbound messages for the given app.
    /// Equivalent to subscribing to `dapps/in/{app}` - but pull-based.
    /// </summary>
    [HttpGet("inbound/{app}")]
    public async Task<ActionResult<List<InboundMessage>>> GetInbound(string app)
    {
        if (!HttpContext.IsAuthorisedForApp(app)) return Forbid();
        var pending = await database.GetUnacknowledgedLocalMessagesForApp(app);
        var now = DateTime.UtcNow;
        return Ok(pending.Select(m => new InboundMessage(
            m.Id, m.FromCallsign, m.Payload,
            TtlMath.Residual(m.Ttl, m.CreatedAt, now),
            string.IsNullOrEmpty(m.OriginatorCallsign) ? null : m.OriginatorCallsign,
            string.IsNullOrEmpty(m.SourceCallsign) ? null : m.SourceCallsign)).ToList());
    }

    /// <summary>
    /// Acknowledge receipt of an inbound message - once ack'd, the message
    /// no longer appears in subsequent /inbound responses or MQTT replays.
    /// Equivalent to publishing the id on `dapps/ack/{app}`. Only the
    /// app's own inbound messages can be acked; any other id (another
    /// app's message, a queued outbound or transit message, or an unknown
    /// id) returns 404 and changes nothing.
    /// </summary>
    [HttpPost("inbound/{app}/{id}/ack")]
    public async Task<IActionResult> Ack(string app, string id)
    {
        if (!HttpContext.IsAuthorisedForApp(app)) return Forbid();
        if (!await database.MarkLocallyDeliveredForApp(app, id)) return NotFound();
        return NoContent();
    }
}

/// <summary>
/// Submit-an-outbound-message request body. <see cref="Ttl"/> is
/// optional residual lifetime in seconds - propagates onto the
/// outgoing <c>ihave</c> as <c>ttl=N</c>. Null = no expiry; the
/// message stays in the queue until forwarded or manually deleted.
/// Apps that care about cleanup should set a value.
///
/// <see cref="StreamId"/> opts the message into per-sender ordered
/// delivery: messages with the same StreamId destined for the same
/// recipient deliver in submit order at the receiver, gated by the
/// receiver's reorder cursor. <see cref="StreamGapTimeoutSeconds"/>
/// chooses the gap policy and is required whenever StreamId is set
/// (#401): 0 = strict (stall forever for the missing seq); &gt;0 =
/// skip the gap after that many seconds. Without a StreamId it is
/// ignored.
/// Both fields ride on the wire as the <c>sid=</c>, <c>sn=</c>,
/// <c>gt=</c> ihave keys; the daemon mints <c>sn</c> automatically
/// from the persisted send-state counter.
/// </summary>
public sealed record OutboundRequest(
    string App,
    string DestCallsign,
    byte[] Payload,
    int? Ttl = null,
    string? StreamId = null,
    uint? StreamGapTimeoutSeconds = null);

public sealed record OutboundResponse(string Id);

/// <summary>
/// One pending inbound message. <see cref="Ttl"/> is the residual
/// lifetime in seconds at the moment of the GET (initial TTL minus
/// dwell time on this node); null when the message has no TTL.
/// <see cref="SourceCallsign"/> is who the message is from, the station
/// to reply to: the originator, or the last hop when the originator is
/// unknown. <see cref="OriginatorCallsign"/> is the originator alone,
/// null when unknown. <see cref="ViaCallsign"/> is the last hop, the
/// neighbour that handed it to us.
/// </summary>
public sealed record InboundMessage(
    string Id, string SourceCallsign, byte[] Payload, int? Ttl = null,
    string? OriginatorCallsign = null, string? ViaCallsign = null);
