using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Transport;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Unit tests for <see cref="NodeProber"/>. Drives the prober with a
/// fake transport that hands back canned receiver bytes, the same
/// pattern as <c>Dappsv1SessionBackhaulTests</c>. Each path corresponds
/// to a distinct ProbeResult shape the dashboard cares about.
/// </summary>
public sealed class NodeProberTests
{
    [Fact]
    public async Task ProbeAsync_DappsPromptObserved_ReturnsSuccess()
    {
        // The prober reads up to the prompt then closes. Anything after
        // the prompt is ignored; the trailing newline triggers the prompt
        // recogniser. (See DappsProtocolClient.ReadInitialPromptAsync.)
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes("DAPPSv1>\n"));
        var prober = MakeProber(transport);

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", bearerPort: 1, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Error.Should().BeEmpty();
        result.Callsign.Should().Be("N0THEM-9");
        result.BearerPort.Should().Be(1);
        result.At.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProbeAsync_WithConnectVia_DialsThatNode_AndReportsThePeer()
    {
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes("Connected to N0THEM\rDAPPSv1>\n"));
        var prober = MakeProber(transport);
        var script = new dapps.client.ConnectScript([new dapps.client.ConnectScriptStep("DAPPS", "DAPPSv1>")]);

        var result = await prober.ProbeAsync("N0US", "N0THEM-3", bearerPort: 1, CancellationToken.None,
            connectScript: script, connectVia: "N0THEM");

        result.Success.Should().BeTrue(result.Error);
        result.Callsign.Should().Be("N0THEM-3");
        transport.Dialled.Should().Equal("N0THEM");
    }

    [Fact]
    public async Task ProbeAsync_TheExchangeLineAfterThePrompt_IsIgnored()
    {
        // A DAPPS node sends its rules straight after the prompt. A probe
        // never starts an exchange, so it just reads past them.
        var transport = new FakeOutboundTransport(Encoding.UTF8.GetBytes(
            "DAPPSv1>\nexchange id=ab12cd hold=120 inline=256 z=1\npeer N0NEXT-9 source=n port=1\nend\n"));
        var prober = MakeProber(transport);

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", bearerPort: 1, CancellationToken.None, fetchPeers: true);

        result.Success.Should().BeTrue();
        result.DiscoveredPeers.Should().ContainSingle().Which.Callsign.Should().Be("N0NEXT-9");
    }

    [Fact]
    public async Task ProbeAsync_NoPrompt_ReturnsFailureWithReason()
    {
        var transport = new FakeOutboundTransport("garbage no prompt"u8.ToArray());
        var prober = MakeProber(transport);

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", 0, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("DAPPSv1>");
    }

    [Fact]
    public async Task ProbeAsync_TransportThrows_ReturnsFailureWithMessage()
    {
        var prober = MakeProber(new ThrowingTransport(new InvalidOperationException("AGW connect rejected")));

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", 0, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("AGW connect rejected");
    }

    [Fact]
    public async Task ProbeAsync_TransportTimesOut_ReturnsTimeoutError()
    {
        // TimeoutException gets a leading "timeout: " prefix so dashboard
        // operators can distinguish a wedged peer from a flat-out reject.
        var prober = MakeProber(new ThrowingTransport(new TimeoutException("AGW: no frame from BPQ for 3 minutes")));

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", 0, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().StartWith("timeout: ");
        result.Error.Should().Contain("3 minutes");
    }

    [Fact]
    public async Task ProbeAsync_CancellationFromCaller_PropagatesAsTaskCanceled()
    {
        // Caller-driven cancellation is a shutdown signal; the scheduler
        // needs the exception to bubble so it can exit ExecuteAsync. Use
        // a transport that blocks indefinitely - the only way out is the
        // cancellation token.
        var prober = MakeProber(new BlockingTransport());
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        Func<Task> act = () => prober.ProbeAsync("N0US", "N0THEM-9", 0, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- A session with the peer already open ----

    /// <summary>An established session with N0THEM-9 (it called us),
    /// the far end driven line by line, in a directory a prober can see.</summary>
    private static async Task<(SessionDirectory Directory, ExchangeSession Session, LinePeer Peer)> OpenSessionAsync(CancellationToken ct)
    {
        var (ours, theirs) = await ExchangeTestKit.LoopbackPairAsync(ct);
        var session = new ExchangeSession(ours, "N0THEM-9", dialled: false, new ExchangeSettings(), new RecordingInbox(), NullLoggerFactory.Instance);
        var peer = new LinePeer(theirs);
        _ = session.RunAsync(ct);
        await peer.ReadLineAsync(ct);   // prompt
        await peer.ReadLineAsync(ct);   // its rules
        await peer.WriteLineAsync("exchange id=peer01 hold=60 inline=256 z=1", ct);
        var deadline = DateTime.UtcNow + ExchangeTestKit.Patience;
        while (!session.Established && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
        var directory = new SessionDirectory();
        directory.Register("N0THEM-9", session);
        return (directory, session, peer);
    }

    [Fact]
    public async Task ProbeAsync_WithASessionOpen_PingsInsideIt_RatherThanDialling()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var (directory, _, peer) = await OpenSessionAsync(lifetime.Token);
        var transport = new FakeOutboundTransport("DAPPSv1>\n"u8.ToArray());
        var prober = new NodeProber(transport, TimeProvider.System, NullLoggerFactory.Instance, NullLogger<NodeProber>.Instance,
            sessionDirectory: directory);

        var probing = prober.ProbeAsync("N0US", "N0THEM-9", bearerPort: 1, lifetime.Token);
        var ping = await peer.ReadLineAsync(lifetime.Token);
        await peer.WriteLineAsync("pong " + ping["ping ".Length..], lifetime.Token);
        var result = await probing;

        result.Success.Should().BeTrue(result.Error);
        result.Detail.Should().Contain("Already in a session with N0THEM-9 (it called us)").And.Contain("answered in");
        transport.Dialled.Should().BeEmpty();
        lifetime.Cancel();
    }

    [Fact]
    public async Task ProbeAsync_WithASessionOpen_AndNoPong_SaysSo()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var (directory, _, _) = await OpenSessionAsync(lifetime.Token);
        var transport = new FakeOutboundTransport("DAPPSv1>\n"u8.ToArray());
        var prober = new NodeProber(transport, TimeProvider.System, NullLoggerFactory.Instance, NullLogger<NodeProber>.Instance,
            sessionDirectory: directory)
        {
            PingTimeout = TimeSpan.FromMilliseconds(300),
        };

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", bearerPort: 1, lifetime.Token);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no answer to a ping").And.Contain("last heard from it");
        result.Detail.Should().Be(result.Error);
        transport.Dialled.Should().BeEmpty();
        lifetime.Cancel();
    }

    [Fact]
    public async Task ProbeAsync_PeerHeldWithNothingRunning_SaysHowLong_AndThatItLooksStuck()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var registry = new PeerSessionRegistry(timeProvider: clock);
        using var lease = registry.Acquire("N0THEM-9", "inbound");
        clock.Advance(TimeSpan.FromMinutes(42));
        var prober = new NodeProber(new ThrowingTransport(new PeerSessionBusyException("N0THEM-9", "inbound")),
            clock, NullLoggerFactory.Instance, NullLogger<NodeProber>.Instance, peerSessions: registry);

        var result = await prober.ProbeAsync("N0US", "N0THEM-9", bearerPort: 1, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("it called us, 42m00s ago").And.Contain("looks stuck");
    }

    [Theory]
    [InlineData(2.4, "2.4s")]
    [InlineData(42, "42s")]
    [InlineData(190, "3m10s")]
    [InlineData(7500, "2h05m")]
    public void Duration_IsShortAndPlain(double seconds, string expected) =>
        NodeProber.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    private static NodeProber MakeProber(IDappsOutboundTransport transport)
        => new(transport, TimeProvider.System, NullLoggerFactory.Instance, NullLogger<NodeProber>.Instance);

    private sealed class FakeOutboundTransport(byte[] cannedReceiverBytes) : IDappsOutboundTransport
    {
        public List<string> Dialled { get; } = [];

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            Dialled.Add(remoteCallsign);
            // FakeDuplexStream rather than a bare MemoryStream - the
            // prober may write back ("peers\n" on Phase 2 fetch-peers
            // probes), which would otherwise overwrite the canned read
            // buffer if read and write shared one stream.
            return Task.FromResult<IDappsConnection>(new FakeConnection(new FakeDuplexStream(cannedReceiverBytes)));
        }

        private sealed class FakeConnection(Stream stream) : IDappsConnection
        {
            public Stream Stream { get; } = stream;
            public ValueTask DisposeAsync() { Stream.Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ThrowingTransport(Exception toThrow) : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
            => Task.FromException<IDappsConnection>(toThrow);
    }

    private sealed class BlockingTransport : IDappsOutboundTransport
    {
        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
        {
            var tcs = new TaskCompletionSource<IDappsConnection>();
            stoppingToken.Register(() => tcs.TrySetCanceled(stoppingToken));
            return tcs.Task;
        }
    }
}
