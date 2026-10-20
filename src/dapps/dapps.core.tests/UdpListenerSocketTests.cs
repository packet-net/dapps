using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// #383: UDP discovery sends from the UDP datagram listener's own socket
/// while it is bound, so an older node that records a beacon's source
/// port learns the port we listen on; beacons advertise only the port the
/// listener actually holds.
/// </summary>
public sealed class UdpListenerSocketTests
{
    [Fact]
    public async Task BeaconAndSolicit_GoOutFromTheListenersPort_WhenItIsBound()
    {
        var (group, channel) = Channel(41880);
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var listenPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var shared = new UdpListenerSocket();
        shared.Attach(listener.Client);
        shared.BoundPort.Should().Be(listenPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var watcher = JoinGroup(group);
        await using var bearer = new UdpMulticastDiscoveryBearer("M0SEND", NullLoggerFactory.Instance, shared);
        await bearer.StartAsync([channel], cts.Token);

        await bearer.AnnounceAsync(new BeaconFrame("M0SEND", 0, 600, new UdpBearerHint("x"), listenPort), group.ToString(), cts.Token);
        var beacon = await ReceiveFrom(watcher, "M0SEND", cts.Token);
        beacon.RemoteEndPoint.Port.Should().Be(listenPort, "an older node records the beacon's source port");

        await bearer.SolicitAsync(new SolicitFrame("M0SEND"), group.ToString(), cts.Token);
        var solicit = await ReceiveFrom(watcher, "solicit", cts.Token);
        solicit.RemoteEndPoint.Port.Should().Be(listenPort);

        // The beacon went to the group's port, not back into the listener as mail.
        listener.Available.Should().Be(0);
    }

    [Fact]
    public async Task WithoutABoundListener_BeaconsStillGo_FromTheBearersOwnSocket()
    {
        var (group, channel) = Channel(42880);
        using var spare = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var shared = new UdpListenerSocket();
        shared.Attach(spare.Client);
        shared.Detach(spare.Client);
        shared.BoundPort.Should().BeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var watcher = JoinGroup(group);
        await using var bearer = new UdpMulticastDiscoveryBearer("M0SEND", NullLoggerFactory.Instance, shared);
        await bearer.StartAsync([channel], cts.Token);

        await bearer.AnnounceAsync(new BeaconFrame("M0SEND", 0, 600, new UdpBearerHint("x")), group.ToString(), cts.Token);
        var got = await ReceiveFrom(watcher, "M0SEND", cts.Token);
        got.RemoteEndPoint.Port.Should().NotBe(((IPEndPoint)spare.Client.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task AListenerThatStopped_FallsBackToTheBearersOwnSocket()
    {
        var (group, channel) = Channel(44880);
        var listener = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var shared = new UdpListenerSocket();
        shared.Attach(listener.Client);
        // Closed without detaching first: the send must not throw.
        listener.Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var watcher = JoinGroup(group);
        await using var bearer = new UdpMulticastDiscoveryBearer("M0SEND", NullLoggerFactory.Instance, shared);
        await bearer.StartAsync([channel], cts.Token);

        await bearer.AnnounceAsync(new BeaconFrame("M0SEND", 0, 600, new UdpBearerHint("x")), group.ToString(), cts.Token);
        await ReceiveFrom(watcher, "M0SEND", cts.Token);
    }

    [Fact]
    public void Detach_OfAnOldSocket_LeavesTheCurrentOneAttached()
    {
        using var a = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        using var b = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var shared = new UdpListenerSocket();
        shared.Attach(a.Client);
        shared.Attach(b.Client);
        shared.Detach(a.Client);
        shared.BoundPort.Should().Be(((IPEndPoint)b.Client.LocalEndPoint!).Port);
    }

    [Fact]
    public void Beacons_AdvertiseOnlyThePortTheListenerHolds()
    {
        var options = new MutableOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "M0SELF", UdpListenPort = 1880 });
        var shared = new UdpListenerSocket();
        var service = new DiscoveryService(
            new Database(NullLogger<Database>.Instance, options), options, TimeProvider.System,
            NullLoggerFactory.Instance, NullLogger<DiscoveryService>.Instance, udpListenerSocket: shared);

        service.UdpPortToAdvertise("udp").Should().BeNull("configured, but nothing is listening on it (yet, or the bind failed)");

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        shared.Attach(listener.Client);
        var held = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        service.UdpPortToAdvertise("udp").Should().Be(held, "the port actually held, not the configured one");
        service.UdpPortToAdvertise("agw").Should().BeNull();

        shared.Detach(listener.Client);
        service.UdpPortToAdvertise("udp").Should().BeNull();
    }

    [Theory]
    [InlineData("DAPPS v1 callsign=M0X hops=0 ttl=60 end", true)]
    [InlineData("DAPPS v1 solicit callsign=M0X end", true)]
    [InlineData("DAPPS v1", true)]
    [InlineData("DAPPS v10", false)]
    [InlineData("abc1234", false)]
    public void DiscoveryFrames_AreRecognisedSoTheListenerDropsThem(string datagram, bool expected)
        => UdpDatagramListener.IsDiscoveryFrame(System.Text.Encoding.ASCII.GetBytes(datagram)).Should().Be(expected);

    private static (IPEndPoint Group, DiscoveryChannelInfo Channel) Channel(int basePort)
    {
        var group = new IPEndPoint(IPAddress.Parse("239.42.42.43"), basePort + Random.Shared.Next(1000));
        return (group, new DiscoveryChannelInfo(
            Id: 1, Bearer: "udp", ChannelKey: group.ToString(), LinkClass: LinkClass.LanMulticast,
            BeaconIntervalSeconds: 60, AdvertisedTtlSeconds: 180, CostHint: 1));
    }

    private static UdpClient JoinGroup(IPEndPoint group)
    {
        var watcher = new UdpClient();
        watcher.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        watcher.Client.Bind(group);
        watcher.JoinMulticastGroup(group.Address);
        return watcher;
    }

    private static async Task<UdpReceiveResult> ReceiveFrom(UdpClient watcher, string containing, CancellationToken ct)
    {
        while (true)
        {
            var r = await watcher.ReceiveAsync(ct);
            if (System.Text.Encoding.ASCII.GetString(r.Buffer).Contains(containing)) return r;
        }
    }
}
