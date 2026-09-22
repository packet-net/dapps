using System.Text;
using System.Text.Json;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace dapps.core.Controllers;

/// <summary>
/// Server-Sent Events surface for the dashboard. The dashboard uses
/// EventSource against <c>/Events/inbound</c> to live-tail messages
/// arriving at this node - handy for watching traffic during RF
/// testing without tailing journald.
///
/// Also serves the dashboard's queue-snapshot JSON endpoint at
/// <c>/Events/queue</c>, polled every few seconds by the dashboard
/// JS so the operator sees the live queue state without a full
/// page reload.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class EventsController(
    InboundEventBus bus,
    Database database,
    IOptionsMonitor<SystemOptions> options,
    OperationalMetrics metrics,
    UpdateChecker updateChecker) : ControllerBase
{
    /// <summary>
    /// Live operational counters + per-neighbour state + recent-event
    /// ring. Polled by the dashboard's Health panel.
    /// </summary>
    [HttpGet("health")]
    public OperationalMetrics.Snapshot GetHealth() => metrics.Take();

    /// <summary>
    /// Running version + latest known release. The dashboard polls this
    /// on a slow cadence and surfaces "v0.X.Y available" if the latest
    /// is newer. Plan C5.1.
    /// </summary>
    [HttpGet("version")]
    public VersionStatus GetVersion()
    {
        var latest = updateChecker.Latest;
        return new VersionStatus(
            Current: updateChecker.Current,
            IsDevBuild: updateChecker.IsDevBuild,
            Latest: latest?.Tag,
            ReleaseUrl: latest?.Url,
            IsAvailable: updateChecker.UpdateAvailable,
            FetchedAt: latest?.FetchedAt);
    }

    /// <summary>
    /// Recently dropped messages - TTL-expired, hash-mismatch, etc. -
    /// soft-deleted from the messages table. Polled by the dashboard's
    /// "Recently dropped" panel for debugging.
    /// </summary>
    [HttpGet("dropped")]
    public async Task<IReadOnlyList<DroppedMessageRow>> GetDropped()
    {
        var rows = await database.GetRecentDroppedMessages(50);
        return rows.Select(d => new DroppedMessageRow(
            Id: d.Id,
            Destination: d.Destination,
            SourceCallsign: d.SourceCallsign,
            Bytes: d.Payload.Length,
            Ttl: d.Ttl,
            CreatedAt: d.CreatedAt,
            DroppedAt: d.DroppedAt,
            Reason: d.Reason)).ToList();
    }

    /// <summary>
    /// Snapshot of the two operational queues (outbound forwards
    /// pending; messages for local apps not yet ack'd) plus
    /// summary counts. Returned as JSON so the dashboard can
    /// rerender the tables in place without a full reload.
    /// </summary>
    [HttpGet("queue")]
    public async Task<QueueSnapshot> GetQueueSnapshot()
    {
        var local = options.CurrentValue.Callsign.Split('-')[0];
        var recent = (await database.GetRecentMessages(50)).ToList();

        bool IsLocal(DbMessage m) =>
            string.Equals(m.Destination.Split('@').Last().Split('-')[0], local, StringComparison.OrdinalIgnoreCase);

        QueueRow Row(DbMessage m, string? appOverride = null) => new(
            Id: m.Id,
            Destination: m.Destination,
            App: appOverride ?? m.Destination.Split('@')[0],
            SourceCallsign: m.SourceCallsign,
            Bytes: m.Payload.Length,
            Ttl: m.Ttl,
            AgeSeconds: (int)Math.Max(0, (DateTime.UtcNow - m.CreatedAt).TotalSeconds));

        var outbound = recent.Where(m => !m.Forwarded && !IsLocal(m)).Select(m => Row(m)).ToList();
        var inbox = recent.Where(m => !m.LocallyDelivered && IsLocal(m))
            .Select(m => Row(m, m.Destination.Split('@')[0]))
            .ToList();

        return new QueueSnapshot(
            TotalMessages: await database.CountMessages(),
            PendingOutbound: await database.CountPendingOutbound(),
            UndeliveredLocal: await database.CountUndeliveredLocal(),
            Outbound: outbound,
            LocalInbox: inbox);
    }

    /// <summary>
    /// Payload preview for a single message id. The dashboard's Messages
    /// page calls this when the operator clicks a row's id to expand it:
    /// keeps the SSE event itself small (no payload bytes flowing through
    /// every tab on every delivery) and lets the preview pull from the
    /// messages table even after the page has been open long enough to
    /// forget which payload corresponded to which row.
    ///
    /// Checks the live <c>messages</c> table first, then falls back to
    /// <c>dropped_messages</c> so the "Dropped" tab can show what a
    /// dropped message actually contained.
    ///
    /// Returns up to <see cref="PayloadPreviewLimit"/> bytes; the rest is
    /// truncated and the row's <c>truncated</c> flag flips on. Body is
    /// presented as both UTF-8 text (with a <c>textValid</c> flag the
    /// page uses to decide whether to fall back to hex) and a hex dump,
    /// so binary payloads still tell the operator something.
    /// </summary>
    [HttpGet("payload/{id}")]
    public async Task<ActionResult<PayloadPreview>> GetPayload(string id)
    {
        var msg = await database.GetMessage(id);
        if (msg is not null)
        {
            return BuildPreview(msg.Id, msg.Destination, msg.SourceCallsign, msg.Payload);
        }

        var dropped = await database.GetDroppedMessage(id);
        if (dropped is not null)
        {
            return BuildPreview(dropped.Id, dropped.Destination, dropped.SourceCallsign, dropped.Payload);
        }

        return NotFound();
    }

    private static PayloadPreview BuildPreview(string id, string destination, string sourceCallsign, byte[]? payload)
    {
        var bytes = payload ?? Array.Empty<byte>();
        var truncated = bytes.Length > PayloadPreviewLimit;
        var slice = truncated ? bytes.AsSpan(0, PayloadPreviewLimit).ToArray() : bytes;

        string? text = null;
        var textValid = false;
        try
        {
            // strict=false would silently mask binary as replacement
            // characters and lie to the operator about what arrived.
            var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = enc.GetString(slice);
            textValid = true;
        }
        catch (DecoderFallbackException)
        {
            text = null;
            textValid = false;
        }

        return new PayloadPreview(
            Id: id,
            Destination: destination,
            SourceCallsign: sourceCallsign,
            ByteLength: bytes.Length,
            Truncated: truncated,
            TextValid: textValid,
            Text: text,
            Hex: Convert.ToHexString(slice));
    }

    private const int PayloadPreviewLimit = 4 * 1024;

    [HttpGet("inbound")]
    public async Task GetInbound(CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        // Buffering off so each event flushes immediately rather than
        // sitting in Kestrel's response buffer.
        Response.Headers["X-Accel-Buffering"] = "no";

        using var sub = bus.Subscribe(out var reader);

        // Initial padding event - some browsers / proxies wait for the
        // first event before fully establishing the stream.
        await Response.WriteAsync(": connected\n\n", ct);
        await Response.Body.FlushAsync(ct);

        // Keepalive comment every 20s - keeps proxies and the browser's
        // EventSource happy on quiet links.
        var keepAlive = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                    await Response.WriteAsync(": keepalive\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
            }
            catch { /* client gone */ }
        }, ct);

        try
        {
            await foreach (var ev in reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(ev);
                await Response.WriteAsync($"event: inbound\ndata: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* normal client disconnect */ }
    }
}

public sealed record QueueRow(
    string Id,
    string Destination,
    string App,
    string SourceCallsign,
    int Bytes,
    int? Ttl,
    int AgeSeconds);

public sealed record QueueSnapshot(
    int TotalMessages,
    int PendingOutbound,
    int UndeliveredLocal,
    IReadOnlyList<QueueRow> Outbound,
    IReadOnlyList<QueueRow> LocalInbox);

public sealed record VersionStatus(
    string Current,
    bool IsDevBuild,
    string? Latest,
    string? ReleaseUrl,
    bool IsAvailable,
    DateTime? FetchedAt);

public sealed record PayloadPreview(
    string Id,
    string Destination,
    string SourceCallsign,
    int ByteLength,
    bool Truncated,
    bool TextValid,
    string? Text,
    string Hex);

public sealed record DroppedMessageRow(
    string Id,
    string Destination,
    string SourceCallsign,
    int Bytes,
    int? Ttl,
    DateTime CreatedAt,
    DateTime DroppedAt,
    string Reason);
