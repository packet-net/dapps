using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests.Integration;

/// <summary>
/// End-to-end test for A1 (TTL forwarding) through real BPQ over the
/// AXIP-UDP-tunneled two-instance topology.
///
/// Flow:
///   1. Pre-load a message into a temp DAPPS DB with <c>Ttl=60</c> and
///      <c>CreatedAt=now-30s</c>, destined for an app on N0BBB.
///   2. Run <see cref="OutboundMessageManager.DoRun"/> wired to a real
///      <see cref="AgwOutboundTransport"/> against BPQ-A.
///   3. A test-side AGW listener registered on BPQ-B as the
///      destination callsign captures the inbound connect, plays the
///      receiver half of the DAPPS protocol just enough to read the
///      ihave line, and asserts the wire bytes carry a residual ttl
///      smaller than the original.
///
/// Without A1's decrement, the wire bytes either omit ttl entirely or
/// carry the original 60s - both fail the assertion.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public class TtlForwardingIntegrationTests(TwoInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-ttl-int-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            // Adding the neighbour is enough - the resolver matches its
            // base callsign against the destination's @-suffix, no
            // separate route hint required (A2).
            c.Insert(new DbNeighbour { Callsign = fixture.ApplCallB, BearerPort = fixture.AxipPortIndex });
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = fixture.ApplCallA,
            DefaultBearerPort = fixture.AxipPortIndex,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ForwardingMessageAcrossBpq_CarriesDecrementedTtlOnTheWire()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = ctSource.Token;

        // ── 1. Start the receiver-side AGW listener on B ───────────────────
        var capturedIhave = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiverTcp = new TcpClient();
        await receiverTcp.ConnectAsync(fixture.Host, fixture.AgwPortB, ct);
        var receiver = new AgwFrameTransport(receiverTcp.GetStream());

        // Register as the destination call on B.
        await receiver.WriteFrameAsync(new AgwFrame(0, 'X', 0, fixture.ApplCallB, "", []), ct);
        var xAck = await receiver.ReadFrameAsync(ct);
        xAck.Kind.Should().Be('X');

        // Run the receiver protocol on a background task so we can drive the
        // sender side of the test in parallel.
        var receiverTask = Task.Run(async () =>
        {
            try
            {
                await ReceiverPlayDappsProtocol(receiver, capturedIhave, ct);
            }
            catch (Exception ex)
            {
                capturedIhave.TrySetException(ex);
            }
        }, ct);

        // ── 2. Pre-load the message into DAPPS-1's DB ──────────────────────
        const string msgId = "ttltest";
        const int originalTtl = 60;
        var queuedAt = DateTime.UtcNow.AddSeconds(-30);
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbMessage
            {
                Id = msgId,
                Payload = "hello-from-A"u8.ToArray(),
                Salt = 1L,
                Destination = $"app@{fixture.CallsignB}",
                SourceCallsign = fixture.CallsignA,
                AdditionalProperties = "{}",
                Ttl = originalTtl,
                CreatedAt = queuedAt,
            });
        }

        // ── 3. Run the forwarder ───────────────────────────────────────────
        var transport = new AgwOutboundTransport(fixture.Host, fixture.AgwPortA, NullLoggerFactory.Instance);
        var backhaul = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = fixture.ApplCallA,
            DefaultBearerPort = fixture.AxipPortIndex,
        });
        var routingContext = new DatabaseRoutingContext(database, optionsMonitor);
        var routingAlgorithm = new StaticRoutingAlgorithm(NullLogger<StaticRoutingAlgorithm>.Instance);
        var manager = new OutboundMessageManager(
            database,
            NullLoggerFactory.Instance,
            optionsMonitor,
            [backhaul],
            routingAlgorithm, routingContext);

        await manager.DoRun(ct);

        // ── 4. Wait for the receiver to capture the ihave line ─────────────
        var ihave = await capturedIhave.Task.WaitAsync(TimeSpan.FromSeconds(40), ct);

        ihave.Should().Contain("ihave " + msgId);
        ihave.Should().MatchRegex(@"\bttl=(\d+)\b");

        var ttl = int.Parse(System.Text.RegularExpressions.Regex.Match(ihave, @"\bttl=(\d+)\b").Groups[1].Value);
        ttl.Should().BeLessThan(originalTtl,
            "the forwarder MUST decrement ttl by the time the message spent in queue");
        ttl.Should().BeGreaterThan(0,
            "30s of dwell against a 60s ttl leaves real headroom - anything ≤0 means we double-counted");

        await receiverTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// Plays the receiver half of the DAPPS protocol just far enough to
    /// read the offer line, ack it, accept the data, and ack again. The
    /// captured offer line is what the test asserts on.
    ///
    /// Operates at the AGW frame level: the sender's stream bytes arrive
    /// as 'D' frames (data records) on our AGW socket; we respond with
    /// 'D' frames carrying the bytes we want to send back.
    /// </summary>
    private async Task ReceiverPlayDappsProtocol(
        AgwFrameTransport receiver,
        TaskCompletionSource<string> capturedIhave,
        CancellationToken ct)
    {
        // 1. Wait for the inbound 'C' frame (BPQ telling us A connected to us).
        var connectFrame = await ReadFrame(receiver, 'C', ct);
        var remoteCall = connectFrame.CallFrom;

        // 2. Send the DAPPSv1> prompt back as a 'D' frame.
        await SendDataBytes(receiver, fixture.ApplCallB, remoteCall, "DAPPSv1>\n"u8.ToArray(), ct);

        // 3. Read 'D' frames until we have a complete line ending in '\n'.
        //    That's the `ihave ...` line we want to capture.
        var ihave = await ReadLine(receiver, ct);
        capturedIhave.TrySetResult(ihave);

        // 4. Reply with `send <id>\n` so the sender will transmit the payload.
        var idMatch = System.Text.RegularExpressions.Regex.Match(ihave, @"^ihave (\S+)");
        if (!idMatch.Success) return;
        var id = idMatch.Groups[1].Value;
        await SendDataBytes(receiver, fixture.ApplCallB, remoteCall,
            Encoding.UTF8.GetBytes($"send {id}\n"), ct);

        // 5. Read the `data <id>\n` line and the payload.
        var dataLine = await ReadLine(receiver, ct);
        // Best-effort length parse from the captured ihave line.
        var lenMatch = System.Text.RegularExpressions.Regex.Match(ihave, @"\blen=(\d+)\b");
        if (!lenMatch.Success || !dataLine.StartsWith("data ", StringComparison.Ordinal)) return;
        var payloadLen = int.Parse(lenMatch.Groups[1].Value);
        await ReadExactBytes(receiver, payloadLen, ct);

        // 6. Ack so the sender's MarkMessageAsForwarded fires.
        await SendDataBytes(receiver, fixture.ApplCallB, remoteCall,
            Encoding.UTF8.GetBytes($"ack {id}\n"), ct);
    }

    private static async Task<AgwFrame> ReadFrame(AgwFrameTransport transport, char kind, CancellationToken ct)
    {
        while (true)
        {
            var frame = await transport.ReadFrameAsync(ct);
            if (frame.Kind == kind) return frame;
        }
    }

    private readonly Queue<byte> _readBuffer = new();

    private async Task<string> ReadLine(AgwFrameTransport transport, CancellationToken ct)
    {
        var line = new List<byte>();
        while (true)
        {
            if (_readBuffer.Count == 0)
            {
                var frame = await ReadFrame(transport, 'D', ct);
                foreach (var b in frame.Payload) _readBuffer.Enqueue(b);
            }
            var nextByte = _readBuffer.Dequeue();
            if (nextByte == (byte)'\n') break;
            line.Add(nextByte);
        }
        return Encoding.UTF8.GetString(line.ToArray());
    }

    private async Task ReadExactBytes(AgwFrameTransport transport, int n, CancellationToken ct)
    {
        var got = 0;
        while (got < n)
        {
            if (_readBuffer.Count == 0)
            {
                var frame = await ReadFrame(transport, 'D', ct);
                foreach (var b in frame.Payload) _readBuffer.Enqueue(b);
            }
            _readBuffer.Dequeue();
            got++;
        }
    }

    private static async Task SendDataBytes(
        AgwFrameTransport transport,
        string callFrom,
        string callTo,
        byte[] payload,
        CancellationToken ct)
    {
        // PID 0xF0 = no-L3 (per AGW spec); port byte is the AXIP carrier.
        await transport.WriteFrameAsync(
            new AgwFrame(1, 'D', 0xF0, callFrom, callTo, payload),
            ct);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
