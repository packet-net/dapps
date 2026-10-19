using AwesomeAssertions;
using dapps.client.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// End-to-end multicast loopback. Two bearer instances on the same
/// loopback interface joined to the same multicast group: one
/// announces, the other hears. Validates the bearer end-to-end on
/// real sockets - Linux loopback supports IP multicast natively, so
/// no fixture / docker is needed.
/// </summary>
public sealed class UdpMulticastDiscoveryTests
{
    [Fact]
    public async Task Loopback_SolicitFromOne_HeardByOtherAsReceivedSolicit()
    {
        // Plan B6.2 - solicit round-trip over the UDP multicast bearer.
        // Sender emits a solicit; receiver yields a ReceivedSolicit (NOT
        // a ReceivedBeacon) carrying the asker's callsign and the same
        // ChannelKey we joined.
        var port = 47880 + Random.Shared.Next(1000);
        var group = $"239.42.42.42:{port}";
        var channel = new DiscoveryChannelInfo(
            Id: 1, Bearer: "udp", ChannelKey: group, LinkClass: LinkClass.LanMulticast,
            BeaconIntervalSeconds: 60, AdvertisedTtlSeconds: 180, CostHint: 1);

        await using var sender = new UdpMulticastDiscoveryBearer("M0SEND", NullLoggerFactory.Instance);
        await using var receiver = new UdpMulticastDiscoveryBearer("M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.StartAsync([channel], cts.Token);
        await receiver.StartAsync([channel], cts.Token);

        var heard = new TaskCompletionSource<ReceivedSolicit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in receiver.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedSolicit rs) continue;
                heard.TrySetResult(rs);
                break;
            }
        }, cts.Token);

        await Task.Delay(150, cts.Token);
        await sender.SolicitAsync(new SolicitFrame("M0SEND"), group, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        got.Solicit.Callsign.Should().Be("M0SEND");
        got.ChannelKey.Should().Be(group);
    }

    [Fact]
    public async Task Loopback_OwnSolicitNotEchoedToSelf()
    {
        // Mirror of the beacon self-echo case: our own solicit looped
        // back via MulticastLoopback must not surface as a
        // ReceivedSolicit, otherwise the service would respond to its
        // own ask.
        var port = 48880 + Random.Shared.Next(1000);
        var group = $"239.42.42.42:{port}";
        var channel = new DiscoveryChannelInfo(1, "udp", group, LinkClass.LanMulticast, 60, 180, 1);

        await using var bearer = new UdpMulticastDiscoveryBearer("M0SELF", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await bearer.StartAsync([channel], cts.Token);

        var saw = new TaskCompletionSource<ReceivedSolicit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedSolicit rs) continue;
                saw.TrySetResult(rs);
                break;
            }
        }, cts.Token);

        await Task.Delay(150, cts.Token);
        await bearer.SolicitAsync(new SolicitFrame("M0SELF"), group, cts.Token);

        var winner = await Task.WhenAny(saw.Task, Task.Delay(TimeSpan.FromSeconds(1), cts.Token));
        winner.Should().NotBeSameAs(saw.Task,
            "a node MUST NOT respond to its own solicit looping back via MulticastLoopback");
    }

    [Fact]
    public async Task Loopback_AnnounceFromOne_HeardByOther()
    {
        var port = 41880 + Random.Shared.Next(1000);
        var group = $"239.42.42.42:{port}";
        var channel = new DiscoveryChannelInfo(
            Id: 1, Bearer: "udp", ChannelKey: group, LinkClass: LinkClass.LanMulticast,
            BeaconIntervalSeconds: 60, AdvertisedTtlSeconds: 180, CostHint: 1);

        await using var sender = new UdpMulticastDiscoveryBearer("M0SEND", NullLoggerFactory.Instance);
        await using var receiver = new UdpMulticastDiscoveryBearer("M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.StartAsync([channel], cts.Token);
        await receiver.StartAsync([channel], cts.Token);

        var heard = new TaskCompletionSource<ReceivedBeacon>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in receiver.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedBeacon rb) continue;
                heard.TrySetResult(rb);
                break;
            }
        }, cts.Token);

        await Task.Delay(150, cts.Token);

        var beacon = new BeaconFrame("M0SEND", Hops: 0, Ttl: 600, Bearer: new AgwBearerHint(0), UdpPort: 4321);
        await sender.AnnounceAsync(beacon, group, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        got.Beacon.Callsign.Should().Be("M0SEND");
        got.Beacon.Hops.Should().Be(0);
        got.Beacon.Ttl.Should().Be(600);
        got.Beacon.Bearer.Should().BeOfType<UdpBearerHint>();
        // #299: the endpoint is the sender's address with the port it says it listens on,
        // not the ephemeral source port of its beacon socket.
        var endpoint = ((UdpBearerHint)got.Beacon.Bearer).Endpoint;
        endpoint.Should().EndWith(":4321");
        got.ChannelKey.Should().Be(group, "the bearer must stamp the channel a beacon arrived on");
    }

    [Fact]
    public async Task Loopback_OwnBeaconNotEchoedToSelf()
    {
        var port = 42880 + Random.Shared.Next(1000);
        var group = $"239.42.42.42:{port}";
        var channel = new DiscoveryChannelInfo(1, "udp", group, LinkClass.LanMulticast, 60, 180, 1);

        await using var bearer = new UdpMulticastDiscoveryBearer("M0SELF", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await bearer.StartAsync([channel], cts.Token);

        var saw = new TaskCompletionSource<ReceivedBeacon>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedBeacon rb) continue;
                saw.TrySetResult(rb);
                break;
            }
        }, cts.Token);

        await Task.Delay(150, cts.Token);
        await bearer.AnnounceAsync(
            new BeaconFrame("M0SELF", 0, 600, new AgwBearerHint(0)),
            group, cts.Token);

        var winner = await Task.WhenAny(saw.Task, Task.Delay(TimeSpan.FromSeconds(1), cts.Token));
        winner.Should().NotBeSameAs(saw.Task,
            "a node MUST NOT add itself to its own peer table even when its own multicast looped back");
    }

    [Fact]
    public async Task TwoChannels_OneBearerInstance_BothJoinAndDeliver()
    {
        // A node could plausibly have two multicast groups: one for the
        // wired LAN, one for a wireless-bridged segment. The bearer must
        // hold both bindings and stamp the right channel on each.
        var portA = 43880 + Random.Shared.Next(1000);
        var portB = 44880 + Random.Shared.Next(1000);
        var groupA = $"239.42.42.42:{portA}";
        var groupB = $"239.42.42.43:{portB}";
        var chA = new DiscoveryChannelInfo(1, "udp", groupA, LinkClass.LanMulticast, 60, 180, 1);
        var chB = new DiscoveryChannelInfo(2, "udp", groupB, LinkClass.LanMulticast, 60, 180, 1);

        await using var sender = new UdpMulticastDiscoveryBearer("M0SEND", NullLoggerFactory.Instance);
        await using var receiver = new UdpMulticastDiscoveryBearer("M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.StartAsync([chA, chB], cts.Token);
        await receiver.StartAsync([chA, chB], cts.Token);

        var seen = new List<ReceivedBeacon>();
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in receiver.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedBeacon rb) continue;
                lock (seen) seen.Add(rb);
                if (seen.Count >= 2) break;
            }
        }, cts.Token);

        await Task.Delay(150, cts.Token);
        await sender.AnnounceAsync(new BeaconFrame("M0SEND", 0, 600, new UdpBearerHint("a"), 4321), groupA, cts.Token);
        await sender.AnnounceAsync(new BeaconFrame("M0SEND", 0, 600, new UdpBearerHint("b"), 4321), groupB, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen) if (seen.Count >= 2) break;
            await Task.Delay(50, cts.Token);
        }

        lock (seen)
        {
            seen.Should().HaveCount(2);
            seen.Select(s => s.ChannelKey).Should().BeEquivalentTo(new[] { groupA, groupB });
        }
    }

    [Fact]
    public async Task TwoChannels_SamePortDifferentGroups_NoCrossChannelLeakage()
    {
        // Regression: when two channels on one bearer instance share a
        // UDP port and differ only in multicast group address, packets
        // sent on one group MUST NOT be delivered as if they arrived on
        // the other. The original bind-to-IPAddress.Any code path leaked
        // because Linux's REUSEADDR + multicast filtering with multiple
        // sockets on the same port routed packets to whichever socket
        // happened to be ready, regardless of group membership. Fixed by
        // binding each recv socket to its multicast group address.
        var port = 46880 + Random.Shared.Next(1000);
        var groupA = $"239.42.42.42:{port}";
        var groupB = $"239.42.42.43:{port}";   // same port, different group
        var chA = new DiscoveryChannelInfo(1, "udp", groupA, LinkClass.LanMulticast, 60, 180, 1);
        var chB = new DiscoveryChannelInfo(2, "udp", groupB, LinkClass.LanMulticast, 60, 180, 1);

        await using var sender = new UdpMulticastDiscoveryBearer("M0SEND", NullLoggerFactory.Instance);
        await using var receiver = new UdpMulticastDiscoveryBearer("M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.StartAsync([chA, chB], cts.Token);
        await receiver.StartAsync([chA, chB], cts.Token);

        var seen = new List<ReceivedBeacon>();
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in receiver.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedBeacon rb) continue;
                lock (seen) seen.Add(rb);
            }
        }, cts.Token);

        await Task.Delay(150, cts.Token);
        // Send distinct beacons on each group so cross-leakage shows up
        // as the wrong (callsign, channel) pairing on the receiver.
        await sender.AnnounceAsync(new BeaconFrame("M0AAAA", 0, 600, new UdpBearerHint("a"), 4321), groupA, cts.Token);
        await sender.AnnounceAsync(new BeaconFrame("M0BBBB", 0, 600, new UdpBearerHint("b"), 4321), groupB, cts.Token);

        // Wait until both expected beacons land (or the deadline passes
        // - if leakage is happening, additional duplicate-mismatch rows
        // would also appear, which we'd detect below).
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen) if (seen.Count >= 2) break;
            await Task.Delay(50, cts.Token);
        }
        // Allow a brief settle so any wrongly-leaked packets have a
        // chance to land too - otherwise we'd assert too early.
        await Task.Delay(250, cts.Token);

        lock (seen)
        {
            // Each callsign must appear ONLY on the channel it was sent
            // on. With the leakage bug, M0AAAA would also appear on
            // groupB (or M0BBBB on groupA) because both recv sockets see
            // both groups' packets.
            seen.Where(s => s.Beacon.Callsign == "M0AAAA")
                .Select(s => s.ChannelKey).Should().Equal(groupA);
            seen.Where(s => s.Beacon.Callsign == "M0BBBB")
                .Select(s => s.ChannelKey).Should().Equal(groupB);
        }
    }

    [Theory]
    [InlineData("not-an-endpoint")]
    [InlineData("239.42.42.42")]
    [InlineData("239.42.42.42:")]
    [InlineData("not-an-ip:1881")]
    [InlineData("239.42.42.42:99999")]
    public async Task BadGroupSpec_LoggedAndSkipped(string badGroup)
    {
        // The bearer logs and skips a malformed channel rather than
        // throwing - one bad config row shouldn't disable the others.
        var goodPort = 45880 + Random.Shared.Next(1000);
        var goodGroup = $"239.42.42.42:{goodPort}";
        var bad = new DiscoveryChannelInfo(1, "udp", badGroup, LinkClass.LanMulticast, 60, 180, 1);
        var good = new DiscoveryChannelInfo(2, "udp", goodGroup, LinkClass.LanMulticast, 60, 180, 1);

        await using var bearer = new UdpMulticastDiscoveryBearer("M0SELF", NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await bearer.StartAsync([bad, good], cts.Token);

        // Good channel should still be usable.
        await bearer.AnnounceAsync(
            new BeaconFrame("M0SELF", 0, 60, new AgwBearerHint(0)),
            goodGroup, cts.Token);

        // Bad channel announce should error.
        var act = async () => await bearer.AnnounceAsync(
            new BeaconFrame("M0SELF", 0, 60, new AgwBearerHint(0)),
            badGroup, cts.Token);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Loopback_BeaconWithoutUdpPort_IsNotSurfaced()
    {
        // #299: an older node's beacon says nothing about where it listens, and its
        // source port is ephemeral, so there is no route to record. A later beacon
        // that does carry a port still arrives.
        var port = 43880 + Random.Shared.Next(1000);
        var group = $"239.42.42.42:{port}";
        var channel = new DiscoveryChannelInfo(
            Id: 1, Bearer: "udp", ChannelKey: group, LinkClass: LinkClass.LanMulticast,
            BeaconIntervalSeconds: 60, AdvertisedTtlSeconds: 180, CostHint: 1);

        await using var sender = new UdpMulticastDiscoveryBearer("M0SEND", NullLoggerFactory.Instance);
        await using var receiver = new UdpMulticastDiscoveryBearer("M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.StartAsync([channel], cts.Token);
        await receiver.StartAsync([channel], cts.Token);

        var heard = new TaskCompletionSource<ReceivedBeacon>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in receiver.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedBeacon rb) continue;
                heard.TrySetResult(rb);
                break;
            }
        }, cts.Token);

        await Task.Delay(150, cts.Token);
        await sender.AnnounceAsync(new BeaconFrame("M0OLD", 0, 600, new UdpBearerHint("x")), group, cts.Token);
        await sender.AnnounceAsync(new BeaconFrame("M0NEW", 0, 600, new UdpBearerHint("x"), 4321), group, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        got.Beacon.Callsign.Should().Be("M0NEW");
    }
}
