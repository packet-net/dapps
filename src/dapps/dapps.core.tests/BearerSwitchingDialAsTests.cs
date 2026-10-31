using System.Text;
using AwesomeAssertions;
using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// #431 over AGW: a call from a Dial as callsign goes out on that
/// callsign's own AGW connection, registered as it (LinBPQ calls from the
/// first callsign a connection registered), while the main connection is
/// left as it is. Each Dial as callsign is its own link in the dial guard.
/// A <see cref="FakeAgwHost"/> plays the node.
/// </summary>
public sealed class BearerSwitchingDialAsTests : IAsyncDisposable
{
    private const string Us = "G0LOCAL-1";
    private const string Remote = "G0REMA-1";
    private readonly List<IAsyncDisposable> owned = [];
    private readonly List<FakeAgwSocket> sockets = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var o in owned) await o.DisposeAsync();
        foreach (var s in sockets) s.Dispose();
    }

    private (BearerSwitchingOutboundTransport Transport, AgwDialAsLinks Pool) Make(FakeAgwHost host, PeerSessionRegistry? peers = null, TimeProvider? clock = null)
    {
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions
        {
            NodeBearer = "agw",
            NodeHost = "127.0.0.1",
            AgwPort = host.Port,
            Callsign = Us,
        });
        var links = new StandaloneAgwLinkSource("127.0.0.1", host.Port, Us, NullLoggerFactory.Instance);
        var pool = new AgwDialAsLinks(options, NullLogger<AgwDialAsLinks>.Instance, AlwaysOpenTxGate.Instance, clock);
        owned.Add(pool);
        owned.Add(links);
        var transport = new BearerSwitchingOutboundTransport(
            options, NullLoggerFactory.Instance, AlwaysOpenTxGate.Instance, TimeProvider.System, TimeSpan.Zero,
            peerSessions: peers, linkSettleSpread: TimeSpan.Zero, agwLinks: links,
            portMaps: new FixedPortMaps(NodePortMap.Plain(3)), dialAsLinks: pool);
        return (transport, pool);
    }

    private async Task<(FakeAgwSocket Socket, string Registered)> AcceptAsync(FakeAgwHost host, CancellationToken ct)
    {
        var socket = await host.AcceptAsync(ct);
        sockets.Add(socket);
        var x = await socket.ExpectRegisterAsync(ct);
        await socket.WriteAsync(ct, FakeAgwSocket.RegisterReply(1));
        return (socket, x.CallFrom);
    }

    private static Task ConfirmAsync(FakeAgwSocket socket, AgwFrame connect, CancellationToken ct) =>
        socket.WriteAsync(ct, new AgwFrame(connect.Port, 'C', 0, connect.CallTo, connect.CallFrom,
            Encoding.ASCII.GetBytes($"*** CONNECTED With Station {connect.CallTo}\r\0")));

    [Fact]
    public async Task ACallFromADialAsCallsign_GoesOnItsOwnConnection_RegisteredAsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (transport, _) = Make(host);

        var connecting = transport.ConnectAsync("g0local-4", Remote, 1, ct);
        var (main, mainCall) = await AcceptAsync(host, ct);
        var (own, ownCall) = await AcceptAsync(host, ct);
        mainCall.Should().Be(Us, "the main connection is the daemon's own, as before");
        ownCall.Should().Be("G0LOCAL-4");

        var connect = await own.ReadUntilAsync('C', ct);
        connect.CallFrom.Should().Be("G0LOCAL-4");
        connect.CallTo.Should().Be(Remote);
        connect.Port.Should().Be((byte)0, "the port byte is worked out from the same node's numbering");
        await ConfirmAsync(own, connect, ct);
        var conn = await connecting;

        (await main.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'C', "nothing is called on the main connection");

        await conn.DisposeAsync();
        var d = await own.ReadUntilAsync('d', ct);
        d.CallFrom.Should().Be("G0LOCAL-4");
    }

    [Fact]
    public async Task TwoDialAsCallsigns_CallOneNodeAtOnce_EachOnItsOwnConnection_AndOneDialAsLinkIsStillOneLink()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var peers = new PeerSessionRegistry();
        var (transport, _) = Make(host, peers);

        var first = transport.ConnectAsync("G0LOCAL-4", Remote, 1, ct);
        await AcceptAsync(host, ct);
        var (four, _) = await AcceptAsync(host, ct);
        var c4 = await four.ReadUntilAsync('C', ct);
        await ConfirmAsync(four, c4, ct);
        await using var conn4 = await first;

        var second = transport.ConnectAsync("G0LOCAL-5", Remote, 1, ct);
        var (five, fiveCall) = await AcceptAsync(host, ct);
        fiveCall.Should().Be("G0LOCAL-5");
        var c5 = await five.ReadUntilAsync('C', ct);
        c5.CallFrom.Should().Be("G0LOCAL-5");
        await ConfirmAsync(five, c5, ct);
        await using var conn5 = await second;

        peers.IsActive(Remote, out _, local: "G0LOCAL-4").Should().BeTrue();
        peers.IsActive(Remote, out _, local: "G0LOCAL-5").Should().BeTrue();
        peers.IsActive(Remote, out _).Should().BeFalse("nothing is open from our own callsign");

        var busy = await FluentActions.Awaiting(() => transport.ConnectAsync("G0LOCAL-4", Remote, 1, ct))
            .Should().ThrowAsync<PeerSessionBusyException>();
        busy.Which.LocalCallsign.Should().Be("G0LOCAL-4");
        busy.Which.Message.Should().Contain("from G0LOCAL-4");
    }

    [Fact]
    public async Task ACallFromACallsignThatIsNotOurs_IsRefused_WithNothingSent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (transport, pool) = Make(host);

        (await FluentActions.Awaiting(() => transport.ConnectAsync("G4ABC-4", Remote, 1, ct))
            .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("not an SSID of this node's callsign").And.Contain("was not called");
        await Task.Delay(300, ct);
        host.Pending().Should().BeFalse("no connection to the node was even opened");
        pool.Open.Should().BeEmpty();
    }

    [Fact]
    public async Task ACallToADialAsCallsign_IsDisconnected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions { NodeHost = "127.0.0.1", AgwPort = host.Port, Callsign = Us });
        var pool = new AgwDialAsLinks(options, NullLogger<AgwDialAsLinks>.Instance);
        owned.Add(pool);

        var acquiring = pool.AcquireAsync("G0LOCAL-4", ct);
        var (own, _) = await AcceptAsync(host, ct);
        using var use = await acquiring;

        await own.WriteAsync(ct, FakeAgwSocket.Connect("G9XYZ-1", "G0LOCAL-4", 0));
        var d = await own.ReadUntilAsync('d', ct);
        d.CallFrom.Should().Be("G0LOCAL-4");
        d.CallTo.Should().Be("G9XYZ-1");
    }

    [Fact]
    public async Task ACallToADialAsCallsign_WhileOurCallOnThatPairIsOnItsWay_IsLeftToTheNode()
    {
        // A 'd' would be applied by callsign pair, and end our own call.
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (transport, _) = Make(host);

        var connecting = transport.ConnectAsync("G0LOCAL-4", Remote, 1, ct);
        await AcceptAsync(host, ct);
        var (own, _) = await AcceptAsync(host, ct);
        var connect = await own.ReadUntilAsync('C', ct);

        await own.WriteAsync(ct, FakeAgwSocket.Connect(Remote, "G0LOCAL-4", 0));
        (await own.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'd');

        await ConfirmAsync(own, connect, ct);
        await using var conn = await connecting;
    }

    [Fact]
    public async Task AConnectionNothingUses_ClosesAfterTwoMinutes_AndTheNextCallOpensItAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var clock = new FakeTimeProvider();
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions { NodeHost = "127.0.0.1", AgwPort = host.Port, Callsign = Us });
        var pool = new AgwDialAsLinks(options, NullLogger<AgwDialAsLinks>.Instance, clock: clock);
        owned.Add(pool);

        var acquiring = pool.AcquireAsync("G0LOCAL-4", ct);
        var (own, _) = await AcceptAsync(host, ct);
        var use = await acquiring;
        var again = await pool.AcquireAsync("G0LOCAL-4", ct);
        use.Link.Should().BeSameAs(again.Link, "one connection per Dial as callsign");

        use.Dispose();
        clock.Advance(AgwDialAsLinks.IdleClose);
        use.Link.Closed.IsCompleted.Should().BeFalse("still in use");

        again.Dispose();
        clock.Advance(AgwDialAsLinks.IdleClose - TimeSpan.FromSeconds(1));
        use.Link.Closed.IsCompleted.Should().BeFalse("not idle for two minutes yet");
        clock.Advance(TimeSpan.FromSeconds(1));
        await use.Link.Closed.WaitAsync(TimeSpan.FromSeconds(5), ct);
        pool.Open.Should().BeEmpty();

        var reopening = pool.AcquireAsync("G0LOCAL-4", ct);
        var (reopened, call) = await AcceptAsync(host, ct);
        call.Should().Be("G0LOCAL-4");
        using var third = await reopening;
        third.Link.Should().NotBeSameAs(use.Link);
    }

    [Fact]
    public async Task ARefusedRegistration_FailsTheCall_AndTheNextTriesAgain()
    {
        // XRouter answers 0x00 when another connection holds the callsign.
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions { NodeHost = "127.0.0.1", AgwPort = host.Port, Callsign = Us });
        var pool = new AgwDialAsLinks(options, NullLogger<AgwDialAsLinks>.Instance);
        owned.Add(pool);

        var acquiring = pool.AcquireAsync("G0LOCAL-4", ct);
        var refusing = await host.AcceptAsync(ct);
        sockets.Add(refusing);
        await refusing.ExpectRegisterAsync(ct);
        await refusing.WriteAsync(ct, FakeAgwSocket.RegisterReply(0));
        (await FluentActions.Awaiting(() => acquiring).Should().ThrowAsync<AgwLinkDownException>())
            .Which.Message.Should().Contain("Dial as G0LOCAL-4");
        pool.Open.Should().BeEmpty();

        var retry = pool.AcquireAsync("G0LOCAL-4", ct);
        await AcceptAsync(host, ct);
        using var use = await retry;
    }
}
