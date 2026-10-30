using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;

namespace dapps.core.tests;

/// <summary>
/// <see cref="AgwLink.OpenNodeSessionAsync"/> (#425) against a scripted
/// node: a session at LinBPQ's own prompt, on the daemon's one AGW
/// connection, alongside the sessions it already carries.
/// </summary>
public sealed class AgwNodeSessionTests
{
    private const string Local = "G5ALF-3";
    private const string Remote = "M0AHN-3";
    private const byte Port = 1;

    private readonly CapturingLoggerFactory logs = new();
    private readonly Channel<AgwLinkSession> inbound = Channel.CreateUnbounded<AgwLinkSession>();

    private sealed class ClosedGate : IDappsTxGate
    {
        public bool TxAllowed => false;
        public string? BlockReason => "operator pressed master TX-stop";
    }

    private static AgwFrame VersionReply(int major, int minor) =>
        new(0, 'R', 0, "", "", [.. BitConverter.GetBytes(major), .. BitConverter.GetBytes(minor)]);

    private async Task<(AgwLink, FakeAgwSocket)> OpenAsync(FakeAgwHost host, CancellationToken ct, IDappsTxGate? gate = null)
    {
        var opening = AgwLink.OpenAsync("127.0.0.1", host.Port, Local, s => inbound.Writer.TryWrite(s), logs.CreateLogger("test"), gate, ct: ct);
        var node = await host.AcceptAsync(ct);
        await node.ExpectRegisterAsync(ct);
        await node.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        await node.ReadUntilAsync('m', ct);
        return (await opening.WaitAsync(FakeAgwHost.FrameTimeout, ct), node);
    }

    [Fact]
    public async Task OnLinbpq_ASessionToSwitchOpens_AndCarriesTheCommand_EvenWithTxStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct, new ClosedGate());
        await using var _ = link;
        using var __ = node;

        var opening = link.OpenNodeSessionAsync(TimeSpan.FromSeconds(10), ct);
        await node.ReadUntilAsync('R', ct);
        await node.WriteAsync(ct, VersionReply(2003, 999));
        var c = await node.ReadUntilAsync('C', ct);
        (c.Port, c.CallFrom, c.CallTo).Should().Be(((byte)0, Local, AgwLink.NodeSessionCallsign));
        await node.WriteAsync(ct, new AgwFrame(0, 'C', 0, AgwLink.NodeSessionCallsign, Local,
            Encoding.ASCII.GetBytes($"*** CONNECTED With Station {AgwLink.NodeSessionCallsign}\0")));
        await using var session = await opening.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        await session.Stream.WriteAsync(Encoding.ASCII.GetBytes("NODES\r"), ct);
        (await node.ReadTextAsync(ct)).Should().Be("NODES\r", "nothing goes on air, so a stopped TX doesn't stop it");
        await node.WriteAsync(ct, FakeAgwSocket.Data(AgwLink.NodeSessionCallsign, Local, 0, NodeTableTextTests.BpqNodes));
        var buffer = new byte[512];
        var n = await session.Stream.ReadAsync(buffer, ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);
        Encoding.ASCII.GetString(buffer, 0, n).Should().Be(NodeTableTextTests.BpqNodes);
    }

    private sealed class OneLinkSource(AgwLink link) : IAgwLinkSource
    {
        public AgwLinkState State => AgwLinkState.Open;
        public AgwLink? TryGetLink() => link;
        public Task<AgwLink?> GetLinkAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult<AgwLink?>(link);
        public Task<AgwLink?> ReopenAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult<AgwLink?>(link);
        public IDisposable SubscribeUi(Action<AgwFrame> onUi) => new MemoryStream();
    }

    [Fact]
    public async Task OnLinbpq_NodesIsAskedWithAStar_SoHiddenNodesAreListedToo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct);
        await using var _ = link;
        using var __ = node;
        var prompt = new NodePrompt(
            new StaticOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Local, NodeBearer = "agw" }),
            new OneLinkSource(link), new Logger<NodePrompt>(logs));

        var asking = prompt.AskAsync("NODES", ct);
        // Asked once to choose the way in, and again before the 'C'.
        await node.ReadUntilAsync('R', ct);
        await node.WriteAsync(ct, VersionReply(2003, 999));
        await node.ReadUntilAsync('R', ct);
        await node.WriteAsync(ct, VersionReply(2003, 999));
        await node.ReadUntilAsync('C', ct);
        await node.WriteAsync(ct, new AgwFrame(0, 'C', 0, AgwLink.NodeSessionCallsign, Local,
            Encoding.ASCII.GetBytes($"*** CONNECTED With Station {AgwLink.NodeSessionCallsign}\0")));

        (await node.ReadTextAsync(ct)).Should().Be("NODES *\r", "LinBPQ's plain NODES hides '#' aliases");
        await node.WriteAsync(ct, FakeAgwSocket.Data(AgwLink.NodeSessionCallsign, Local, 0, "LOC:G9LOC} Nodes\r#CAL:G9CAL          \r"));
        var reply = await asking.WaitAsync(TimeSpan.FromSeconds(10), ct);

        NodeTableText.Find(NodeTableText.ParseNodes(reply), "#CAL")!.ToString().Should().Be("#CAL:G9CAL");
    }

    [Fact]
    public async Task OnAnythingButLinbpq_NoCallGoesOut()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct);
        await using var _ = link;
        using var __ = node;

        var opening = link.OpenNodeSessionAsync(TimeSpan.FromSeconds(10), ct);
        await node.ReadUntilAsync('R', ct);
        await node.WriteAsync(ct, VersionReply(2000, 20));

        var refused = await FluentActions.Awaiting(() => opening.WaitAsync(FakeAgwHost.FrameTimeout, ct))
            .Should().ThrowAsync<NotSupportedException>();
        refused.Which.Message.Should().Contain("2000.20");
        (await node.DrainAsync(ct)).Should().NotContain(f => f.Kind == 'C', "XRouter or Dire Wolf would send SABMs to a station called SWITCH");
    }

    [Fact]
    public async Task ANodeSession_LeavesAnInboundSessionsFramesWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var (link, node) = await OpenAsync(host, ct);
        await using var _ = link;
        using var __ = node;

        await node.WriteAsync(ct, FakeAgwSocket.Connect(Remote, Local, Port));
        var peer = await inbound.Reader.ReadAsync(ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);

        var opening = link.OpenNodeSessionAsync(TimeSpan.FromSeconds(10), ct);
        await node.ReadUntilAsync('R', ct);
        await node.WriteAsync(ct, VersionReply(2003, 999));
        await node.ReadUntilAsync('C', ct);
        await node.WriteAsync(ct, new AgwFrame(0, 'C', 0, AgwLink.NodeSessionCallsign, Local,
            Encoding.ASCII.GetBytes($"*** CONNECTED With Station {AgwLink.NodeSessionCallsign}\0")));
        var session = await opening.WaitAsync(FakeAgwHost.FrameTimeout, ct);

        await node.WriteAsync(ct,
            FakeAgwSocket.Data(Remote, Local, Port, "for the peer's session"),
            FakeAgwSocket.Data(AgwLink.NodeSessionCallsign, Local, 0, "for the node session"));
        var buffer = new byte[256];
        var n = await peer.Stream.ReadAsync(buffer, ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);
        Encoding.ASCII.GetString(buffer, 0, n).Should().Be("for the peer's session");
        n = await session.Stream.ReadAsync(buffer, ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);
        Encoding.ASCII.GetString(buffer, 0, n).Should().Be("for the node session");

        await session.DisposeAsync();
        var d = await node.ReadUntilAsync('d', ct);
        (d.CallFrom, d.CallTo).Should().Be((Local, AgwLink.NodeSessionCallsign), "closing the node session names only its own key");
        peer.Stream.RemoteClosed.IsCancellationRequested.Should().BeFalse();
    }
}
