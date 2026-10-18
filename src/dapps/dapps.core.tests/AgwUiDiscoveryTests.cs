using System.Text;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="AgwUiDiscoveryBearer"/> on the daemon's one AGW
/// connection (#272): the <see cref="AgwInboundService"/>'s link, against
/// a <see cref="FakeAgwSocket"/> playing BPQ. Beacons go out on the link
/// from its callsign, are heard on the configured ports only, keep being
/// heard across reconnects, are not sent or held back while it is down,
/// and with another bearer the link only listens.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AgwUiDiscoveryTests
{
    private const string Local = "G5ALF-3";
    private const string Remote = "M0AHN-3";

    private static DiscoveryChannelInfo Channel(int id, byte port) =>
        new(id, "agw", port.ToString(), LinkClass.VhfUhfFm,
            BeaconIntervalSeconds: 1800, AdvertisedTtlSeconds: 5400, CostHint: 5);

    [Fact]
    public async Task Beacons_GoOutOnTheLink_FromItsCallsign_WithNoSocketRegistrationOrMonitorOfTheirOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 0), Channel(2, 1)], ct);

        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 600, new AgwBearerHint(1)), "1", ct);
        var beacon = await bpq.ReadUntilAsync('M', ct);
        (beacon.Port, beacon.CallFrom, beacon.CallTo, beacon.Pid).Should().Be(((byte)1, Local, "DAPPS", (byte)0xF0));
        Encoding.ASCII.GetString(beacon.Payload).Should().StartWith("DAPPS v1");

        await bearer.SolicitAsync(new SolicitFrame(Local), "0", ct);
        var solicit = await bpq.ReadUntilAsync('M', ct);
        (solicit.Port, solicit.CallFrom).Should().Be(((byte)0, Local));
        Encoding.ASCII.GetString(solicit.Payload).Should().StartWith("DAPPS v1 solicit ");

        // #274: BPQ's 'm' toggles one flag per socket; the link sent the one.
        bpq.Received.Where(f => f.Kind != 'G').Select(f => f.Kind).Should().Equal(['X', 'm', 'M', 'M']);
        h.Host.Pending().Should().BeFalse("the bearer opens no socket of its own");
    }

    [Fact]
    public async Task OnlyUiFramesOnTheConfiguredPorts_AreHeard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        var bpq = await h.StartAsync(ct);
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1), Channel(2, 3)], ct);
        await using var heard = bearer.ListenAsync(ct).GetAsyncEnumerator(ct);

        await bpq.WriteAsync(ct,
            Ui(2, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600"),      // no channel on port 2
            Ui(1, Local, $"DAPPS v1 callsign={Local} hops=0 ttl=600"),          // our own
            new AgwFrame(1, 'S', 0, "G7XYZ-9", "DAPPS", Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600")),
            new AgwFrame(1, 'T', 0xF0, "G7XYZ-9", "DAPPS", Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600")),
            Monitored(1, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=1 ttl=600"),
            Ui(3, "G0AXP", "DAPPS v1 solicit callsign=G0AXP"),
            Ui(3, "M0END", "DAPPS v1 callsign=M0END hops=0 ttl=600"));

        var first = (ReceivedBeacon)await NextAsync(heard, ct);
        (first.Beacon.Callsign, first.ChannelKey, first.Beacon.Hops).Should().Be(("G7XYZ-9", "1", 1));
        ((AgwBearerHint)first.Beacon.Bearer).BearerPort.Should().Be(1);
        var second = (ReceivedSolicit)await NextAsync(heard, ct);
        (second.Solicit.Callsign, second.ChannelKey).Should().Be(("G0AXP", "3"));
        var third = (ReceivedBeacon)await NextAsync(heard, ct);
        third.Beacon.Callsign.Should().Be("M0END", "nothing else in between was a UI frame from a peer on a configured port");
    }

    [Fact]
    public async Task WhileTheLinkIsDown_NothingIsSent_AndTheSubscriptionHearsTheNextLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        var bpq = await h.StartAsync(ct);
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1)], ct);
        await using var heard = bearer.ListenAsync(ct).GetAsyncEnumerator(ct);

        bpq.Close();
        await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);
        bearer.Connection.Should().BeNull();
        await FluentActions.Awaiting(() => bearer.AnnounceAsync(new BeaconFrame(Local, 0, 111, new AgwBearerHint(1)), "1", ct)
                .WaitAsync(FakeAgwHost.FrameTimeout, ct))
            .Should().ThrowAsync<AgwLinkDownException>();

        clock.Advance(ReconnectBackoffSchedule.DelayForAttempt(1));
        using var again = await AcceptRegisteredAsync(h.Host, ct);
        var link = await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);
        bearer.Connection.Should().BeSameAs(link);

        await again.WriteAsync(ct, Ui(1, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600"));
        ((ReceivedBeacon)await NextAsync(heard, ct)).Beacon.Callsign.Should().Be("G7XYZ-9");

        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 222, new AgwBearerHint(1)), "1", ct);
        var beacon = await again.ReadUntilAsync('M', ct);
        Encoding.ASCII.GetString(beacon.Payload).Should().Contain("ttl=222", "the beacon tried while the link was down was not held back");
        again.Received.Count(f => f.Kind == 'M').Should().Be(1);
    }

    [Fact]
    public async Task WithAnotherBearer_TheLinkOnlyListens_SendingAndHearingBeacons_AndTakingNoCalls()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1)], ct);
        await using var heard = bearer.ListenAsync(ct).GetAsyncEnumerator(ct);

        await h.Service.StartAsync(ct);
        using var bpq = await h.Host.AcceptAsync(ct);
        (await bpq.ReadFrameAsync(ct)).Kind.Should().Be('m', "a listen-only link registers no callsign");
        var link = await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);
        link!.ListenOnly.Should().BeTrue();
        h.Service.State.Should().Be(AgwLinkState.Open);

        // BPQ sends an 'M' from the header's CallFrom, registered or not.
        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 600, new AgwBearerHint(1)), "1", ct);
        (await bpq.ReadUntilAsync('M', ct)).CallFrom.Should().Be(Local);
        await bpq.WriteAsync(ct, Ui(1, "G7XYZ-9", "DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600"));
        ((ReceivedBeacon)await NextAsync(heard, ct)).Beacon.Callsign.Should().Be("G7XYZ-9");

        // A call reaching it all the same is let go, with no prompt.
        await bpq.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, 1));
        var bye = await bpq.ReadUntilAsync('d', ct);
        (bye.CallFrom, bye.CallTo, bye.Port).Should().Be((Local, Remote, (byte)1));

        // Nor does it call out.
        (await FluentActions.Awaiting(() => new AgwOutboundTransport(h.Service, h.Logs).ConnectAsync(Local, Remote, 1, ct))
            .Should().ThrowAsync<AgwLinkDownException>()).Which.Message.Should().Contain("only listens");
        await FluentActions.Awaiting(() => link.DialAsync(1, Local, Remote, TimeSpan.FromMinutes(3), ct))
            .Should().ThrowAsync<InvalidOperationException>();

        (await bpq.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'C' || f.Kind == 'D' || f.Kind == 'X');
        bpq.Received.Should().NotContain(f => f.Kind == 'X' || f.Kind == 'D');
        h.Metrics.Take().AgwReconnects.Should().Be(0, "nothing was registered");
    }

    [Fact]
    public async Task ABearerSwitch_ReopensTheLinkListenOnly_AndBackWithItsRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservableTimeProvider();
        await using var h = new AgwInboundServiceHarness(Local, clock);
        await using var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1)], ct);
        using var full = await h.StartAsync(ct);

        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        await clock.WaitForTimerAsync(AgwInboundService.NonFailureRetryDelay, ct);
        clock.Advance(AgwInboundService.NonFailureRetryDelay);
        using var listening = await h.Host.AcceptAsync(ct);
        (await listening.ReadFrameAsync(ct)).Kind.Should().Be('m');
        (await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct))!.ListenOnly.Should().BeTrue();
        await bearer.AnnounceAsync(new BeaconFrame(Local, 0, 600, new AgwBearerHint(1)), "1", ct);
        (await listening.ReadUntilAsync('M', ct)).CallFrom.Should().Be(Local);

        h.Options.Set(h.With(o => o.NodeBearer = "agw"));
        await clock.WaitForTimerAsync(AgwInboundService.NonFailureRetryDelay, ct);
        clock.Advance(AgwInboundService.NonFailureRetryDelay);
        using var registered = await AcceptRegisteredAsync(h.Host, ct);
        (await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct))!.ListenOnly.Should().BeFalse();
    }

    [Fact]
    public async Task AListenOnlyLink_ClosesOnceNoDiscoveryChannelWantsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = new AgwInboundServiceHarness(Local);
        h.Options.Set(h.With(o => o.NodeBearer = "rhpv2"));
        var bearer = new AgwUiDiscoveryBearer(h.Service, h.Logs);
        await bearer.StartAsync([Channel(1, 1)], ct);
        await h.Service.StartAsync(ct);
        using var bpq = await h.Host.AcceptAsync(ct);
        await bpq.ReadUntilAsync('m', ct);
        await h.Service.GetLinkAsync(FakeAgwHost.FrameTimeout, ct);

        await bearer.DisposeAsync();
        await h.Logs.WaitForAsync("connection withdrawn (Idle)", ct);
        h.Service.State.Should().Be(AgwLinkState.Idle);
        h.Service.TryGetLink().Should().BeNull();
    }

    [Fact]
    public async Task ACallsignChange_SendsTheNextBeaconFromTheNewCallsign_AtOnce()
    {
        // #274: the bearer used to keep the callsign it was built with.
        // It now has none of its own: the link reconnects under the new
        // one, and the service announces at once on the new link.
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async () =>
        {
            await using var h = new AgwInboundServiceHarness("M0OLD-1");
            var service = await StartDiscoveryAsync(h, beaconIntervalSeconds: 1800, ct);
            try
            {
                using var first = await h.StartAsync(ct);
                (await first.ReadUntilAsync('M', ct)).CallFrom.Should().Be("M0OLD-1");

                h.Options.Set(h.With(o => o.Callsign = "M0NEW-1"));

                using var second = await h.Host.AcceptAsync(ct);
                (await second.ExpectRegisterAsync(ct)).CallFrom.Should().Be("M0NEW-1");
                await second.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
                var beacon = await second.ReadUntilAsync('M', ct);
                beacon.CallFrom.Should().Be("M0NEW-1", "beacons go out as the new callsign");
                Encoding.ASCII.GetString(beacon.Payload).Should().Contain("callsign=M0NEW-1");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        });
    }

    [Fact]
    public async Task WhileTheLinkIsDown_NoBeaconIsTried_AndOneGoesWhenItIsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async () =>
        {
            var clock = new ObservableTimeProvider();
            await using var h = new AgwInboundServiceHarness(Local, clock);
            var service = await StartDiscoveryAsync(h, beaconIntervalSeconds: 5, ct);
            try
            {
                var bpq = await h.StartAsync(ct);
                await bpq.ReadUntilAsync('M', ct);
                bpq.Close();
                await clock.WaitForTimerAsync(ReconnectBackoffSchedule.DelayForAttempt(1), ct);

                // More than two beacon intervals with the link down.
                await Task.Delay(TimeSpan.FromSeconds(11), ct);
                h.Logs.Any("announce on agw/1 failed").Should().BeFalse("nothing is tried while the link is down");

                clock.Advance(ReconnectBackoffSchedule.DelayForAttempt(1));
                using var again = await AcceptRegisteredAsync(h.Host, ct);
                await again.ReadUntilAsync('M', ct);
                (await again.DrainAsync(ct, TimeSpan.FromSeconds(2))).Should().NotContain(f => f.Kind == 'M',
                    "one beacon on the new link, not one for each interval missed");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        });
    }

    private static async Task<DiscoveryService> StartDiscoveryAsync(AgwInboundServiceHarness h, int beaconIntervalSeconds, CancellationToken ct)
    {
        var database = new Database(NullLogger<Database>.Instance, h.Options);
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.VhfUhfFm, BeaconIntervalSeconds = beaconIntervalSeconds,
        });
        var service = new DiscoveryService(
            database, h.Options, TimeProvider.System, h.Logs, new Logger<DiscoveryService>(h.Logs), agwLinks: h.Service)
        {
            ChannelReloadInterval = TimeSpan.FromMilliseconds(100),
        };
        await service.StartAsync(ct);
        return service;
    }

    private static async Task WithDatabaseAsync(Func<Task> test)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dapps-agw-disc-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        try
        {
            using (var c = DbInfo.GetConnection())
            {
                c.CreateTable<DbDiscoveredPeer>();
                c.CreateTable<DbDiscoveryChannel>();
            }
            await test();
        }
        finally
        {
            DbInfo.OverridePath = null;
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    /// <summary>Accepts the service's next connection and plays BPQ up to
    /// its monitor 'm'.</summary>
    private static async Task<FakeAgwSocket> AcceptRegisteredAsync(FakeAgwHost host, CancellationToken ct)
    {
        var socket = await host.AcceptAsync(ct);
        await socket.ExpectRegisterAsync(ct);
        await socket.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        await socket.ReadUntilAsync('m', ct);
        return socket;
    }

    private static async Task<ReceivedDiscoveryFrame> NextAsync(IAsyncEnumerator<ReceivedDiscoveryFrame> heard, CancellationToken ct)
    {
        (await heard.MoveNextAsync().AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct)).Should().BeTrue();
        return heard.Current;
    }

    /// <summary>A UI frame's bare data, as the tests' node has always sent
    /// it. The header's CallTo is whatever BPQ last put there.</summary>
    private static AgwFrame Ui(byte port, string from, string text) =>
        new(port, 'U', 0xF0, from, "STALE", Encoding.ASCII.GetBytes(text));

    /// <summary>A UI frame as BPQ's monitor hands it over: a header line
    /// numbering the port from 1, a CR, the data, a CR.</summary>
    private static AgwFrame Monitored(byte port, string from, string text) =>
        new(port, 'U', 0xF0, from, "STALE",
            Encoding.ASCII.GetBytes($" {port + 1}:Fm {from} To DAPPS <UI pid=F0 Len={text.Length}>[12:00:00]\r{text}\r\0"));
}
