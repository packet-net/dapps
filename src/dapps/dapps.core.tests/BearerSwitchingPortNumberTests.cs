using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// A call over AGW goes out on the AGW port byte for the node's own port
/// number (#233, #370), worked out from how the node numbers its ports
/// (<see cref="NodePortMap"/>), and nothing goes out for a port the node
/// doesn't have or whose numbering can't be read. A
/// <see cref="FakeAgwSocket"/> plays the node.
/// </summary>
public sealed class BearerSwitchingPortNumberTests : IAsyncDisposable
{
    private const string Local = "G0LOCAL-1";
    private const string Remote = "G0REMA-1";
    private StandaloneAgwLinkSource? links;
    private FakeAgwSocket? node;

    [Theory]
    [InlineData(19, 2)]
    [InlineData(2, 0)]
    [InlineData(7, 1)]
    public async Task OnAGappedXrouter_ThePortNumber_IsCalledOnItsPositionAmongThePorts(int number, byte agwByte)
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var transport = Make(host, new FixedPortMaps(NodePortMap.Xrouter([2, 7, 19])));

        var connecting = transport.ConnectAsync(Local, Remote, number, ct);
        var connect = await AcceptAndReadConnectAsync(host, ct);

        connect.Port.Should().Be(agwByte, "XRouter's AGW counts PORT=2, 7, 19 as 0, 1, 2; +1 would have used another radio");
        await ConfirmAsync(connect, ct);
        await using var conn = await connecting;
    }

    [Fact]
    public async Task OnBpq_PortNumber_IsTheByteLessOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var transport = Make(host, new FixedPortMaps(NodePortMap.Plain(3)));

        var connecting = transport.ConnectAsync(Local, Remote, 3, ct);
        var connect = await AcceptAndReadConnectAsync(host, ct);

        connect.Port.Should().Be((byte)2, "BPQ's PORTNUM 3 is its AGW port byte 2 when ports are numbered 1..N");
        await ConfirmAsync(connect, ct);
        await using var conn = await connecting;
    }

    [Theory]
    [InlineData(3)]  // XRouter has no PORT=3
    [InlineData(0)]
    public async Task APortTheNodeDoesNotHave_IsNotCalled(int number)
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var transport = Make(host, new FixedPortMaps(NodePortMap.Xrouter([2, 7, 19])));

        var connecting = transport.ConnectAsync(Local, Remote, number, ct);
        await AcceptRegisteredAsync(host, ct);

        (await FluentActions.Awaiting(() => connecting).Should().ThrowAsync<AgwLinkDownException>())
            .Which.Message.Should().Contain($"no port {number}").And.Contain("was not called");
        (await node!.DrainAsync(ct, TimeSpan.FromMilliseconds(500))).Should().NotContain(f => f.Kind == 'C');
    }

    [Fact]
    public async Task APortPastBpqsList_IsNotCalled()
    {
        // BPQ connects a byte past its visible ports on its first port.
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var transport = Make(host, new FixedPortMaps(NodePortMap.Plain(2)));

        var connecting = transport.ConnectAsync(Local, Remote, 3, ct);
        await AcceptRegisteredAsync(host, ct);

        await FluentActions.Awaiting(() => connecting).Should().ThrowAsync<AgwLinkDownException>();
        (await node!.DrainAsync(ct, TimeSpan.FromMilliseconds(500))).Should().NotContain(f => f.Kind == 'C');
    }

    [Fact]
    public async Task AnXrouterWhoseNumbersCantBeRead_IsNotCalledOnAGuessedPort()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new FakeAgwHost();
        var transport = Make(host, new FixedPortMaps(null));

        var connecting = transport.ConnectAsync(Local, Remote, 1, ct);
        await AcceptRegisteredAsync(host, ct);

        (await FluentActions.Awaiting(() => connecting).Should().ThrowAsync<AgwLinkDownException>())
            .Which.Message.Should().Contain("could not be read");
        (await node!.DrainAsync(ct, TimeSpan.FromMilliseconds(500))).Should().NotContain(f => f.Kind == 'C');
    }

    public async ValueTask DisposeAsync()
    {
        if (links is not null) await links.DisposeAsync();
        node?.Dispose();
    }

    private BearerSwitchingOutboundTransport Make(FakeAgwHost host, INodePortMaps maps)
    {
        var options = new StaticOptionsMonitor<SystemOptions>(new SystemOptions
        {
            NodeBearer = "agw",
            NodeHost = "127.0.0.1",
            AgwPort = host.Port,
        });
        links = new StandaloneAgwLinkSource("127.0.0.1", host.Port, Local, NullLoggerFactory.Instance);
        return new BearerSwitchingOutboundTransport(
            options, NullLoggerFactory.Instance, AlwaysOpenTxGate.Instance, TimeProvider.System, TimeSpan.Zero,
            linkSettleSpread: TimeSpan.Zero, agwLinks: links, portMaps: maps);
    }

    private async Task AcceptRegisteredAsync(FakeAgwHost host, CancellationToken ct)
    {
        node = await host.AcceptAsync(ct);
        await node.ExpectRegisterAsync(ct);
        await node.WriteAsync(ct, new AgwFrame(0, 'X', 0, "", "", [1]));
    }

    private async Task<AgwFrame> AcceptAndReadConnectAsync(FakeAgwHost host, CancellationToken ct)
    {
        await AcceptRegisteredAsync(host, ct);
        return await node!.ReadUntilAsync('C', ct);
    }

    private async Task ConfirmAsync(AgwFrame connect, CancellationToken ct) =>
        await node!.WriteAsync(ct, new AgwFrame(
            connect.Port, 'C', 0, connect.CallTo, connect.CallFrom,
            Encoding.ASCII.GetBytes($"*** CONNECTED With Station {connect.CallTo}\r\0")));
}
