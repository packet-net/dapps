using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests.Integration;

/// <summary>
/// End-to-end inbound delivery using the **AGW** path on both sides.
/// Replaces the old BPQ-Apps-Interface (HOST/CMDPORT TCP-bridge) E2E
/// - dapps now uses one BPQ surface (AGW) for both inbound and
/// outbound, removing the same-host constraint and the LF→CR
/// rewriting Telnet-driver quirks.
///
/// Flow:
///
///     real Dappsv1SessionBackhaul (sender)
///        ─AGW─▶ BPQ-A
///                ─AXIP-UDP─▶ BPQ-B
///                              ─AGW dispatch─▶ AgwInboundService (in-proc)
///                                                ─▶ InboundConnectionHandler
///                                                    ─▶ IBackhaulInbox capture
///
/// Assertion: the received <see cref="BackhaulMessage"/> has the right
/// id / payload / salt / destination, and <c>sourceCallsign</c> stamped
/// onto the inbox call equals the sender's APPL callsign - the contract
/// that flows through to <c>dapps-source</c> on app-interface delivery.
///
/// BPQ config note: the existing <see cref="TwoInstanceLinbpqFixture"/>
/// uses <c>APPL1CALL</c> on both sides with no APPLICATION line CMD -
/// exactly what AGW dispatch wants (the call goes onto BPQ's L2
/// listen-list as part of the APPL config, and the AGW client picks it
/// up via the <c>'X'</c> register). <see cref="TwoInstanceAgwSmokeTests"/>
/// already proved that dispatch path; this test adds DAPPS receiver
/// behaviour on top.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public sealed class AgwInboundDeliveryTests(TwoInstanceLinbpqFixture fixture) : IAsyncLifetime
{
    private string _dbPath = null!;
    private CapturingInbox _inbox = null!;
    private AgwInboundService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dapps-agw-inbound-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = _dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbMessage>();
            c.CreateTable<DbReceived>();
        }

        // Receiver-side dapps: the AGW inbound service registers as
        // ApplCallB on BPQ-B's AGW, so any L2 SABM addressed to that
        // callsign gets dispatched to us as an inbound 'C' frame.
        var receiverOptions = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = fixture.ApplCallB,
            NodeHost = fixture.Host,
            AgwPort = fixture.AgwPortB,
        });
        var database = new Database(NullLogger<Database>.Instance, receiverOptions);
        _inbox = new CapturingInbox();
        _service = new AgwInboundService(
            receiverOptions, database, _inbox,
            NullLoggerFactory.Instance, NullLogger<AgwInboundService>.Instance);
        await _service.StartAsync(CancellationToken.None);

        // Connect-and-register completes asynchronously; small grace
        // before driving the sender side.
        await Task.Delay(500);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        // Settle-delay: closing our AGW socket sends a FIN, but BPQ may
        // not finish releasing our 'X' registration / any half-up L2
        // session state before the next test in this collection (e.g.
        // TtlForwardingIntegrationTests) opens its own AGW socket and
        // tries to register the same callsign. Without this pause, the
        // next test's inbound 'C' can still be dispatched to our
        // already-closing socket and never reach the new registrant.
        await Task.Delay(2000);
        DbInfo.OverridePath = null;
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }

    [Fact]
    public async Task SenderForwardsViaAgw_MessageLandsInReceiverInbox()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = ctSource.Token;

        var payload = "hello-via-agw-inbound"u8.ToArray();
        const long salt = 99L;
        var id = DappsMessage.ComputeHash(payload, salt)[..7];

        var bearerMsg = new BackhaulMessage(
            Id: id,
            Destination: $"app@{fixture.CallsignB}",
            Salt: salt,
            Ttl: null,
            Payload: payload,
            Headers: null);

        var transport = new AgwOutboundTransport(fixture.Host, fixture.AgwPortA, NullLoggerFactory.Instance);
        var bearer = new Dappsv1SessionBackhaul(transport, NullLoggerFactory.Instance);
        var route = new BackhaulRoute(
            Callsign: fixture.ApplCallB,
            BearerPort: fixture.AxipPortIndex);

        var sendResult = await bearer.SendAsync(bearerMsg, route, fixture.ApplCallA, ct);
        sendResult.Accepted.Should().BeTrue(
            $"the sender bearer should report acceptance when the receiver acks; failure was: {sendResult.Error}");

        var captured = await _inbox.WaitForFirstAsync(TimeSpan.FromSeconds(30), ct);

        captured.Message.Id.Should().Be(id);
        captured.Message.Payload.Should().BeEquivalentTo(payload,
            "payload bytes round-trip intact through AGW outbound → AXIP → AGW inbound dispatch");
        captured.Message.Salt.Should().Be(salt);
        captured.Message.Destination.Should().Be($"app@{fixture.CallsignB}");

        captured.SourceCallsign.Should().Be(fixture.ApplCallA,
            "the AGW inbound dispatcher reads CallFrom off the 'C' frame and stamps it onto the inbox " +
            "call - that's what dapps-source surfaces to subscribed apps");
    }

    private sealed record InboxCapture(BackhaulMessage Message, string SourceCallsign);

    private sealed class CapturingInbox : IBackhaulInbox
    {
        private readonly TaskCompletionSource<InboxCapture> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        {
            _tcs.TrySetResult(new InboxCapture(message, sourceCallsign));
            return Task.CompletedTask;
        }

        public Task<InboxCapture> WaitForFirstAsync(TimeSpan timeout, CancellationToken ct) =>
            _tcs.Task.WaitAsync(timeout, ct);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
