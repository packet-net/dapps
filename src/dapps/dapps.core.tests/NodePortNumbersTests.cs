using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using RhpV2.Client;
using RhpV2.Client.Protocol;
using RhpV2.Client.Testing;

namespace dapps.core.tests;

/// <summary>
/// How DAPPS turns the port number an operator sees on the node into the
/// AGW port byte and back (#233, #370): <see cref="NodePortMap"/>, and
/// <see cref="NodePortNumbers"/>, which works it out from the node's
/// answer to a 'G' and, for XRouter, its RHPv2 port. XRouter's AGW counts
/// its ports from 0 in order of their <c>PORT=</c> numbers and never says
/// which numbers they are; BPQ, Dire Wolf and AGWPE count 1, 2, 3 in AGW
/// order.
/// </summary>
public sealed class NodePortNumbersTests
{
    private const string Local = "G5ALF-3";

    // ── NodePortMap ───────────────────────────────────────────────

    [Fact]
    public void APlainHost_ByteIsNumberLessOne_AndAPortPastItsListIsRefused()
    {
        var map = NodePortMap.Plain(3);
        map.ToAgwPort(1).Should().Be((byte)0);
        map.ToAgwPort(3).Should().Be((byte)2);
        map.ToAgwPort(4).Should().BeNull("BPQ sends an unknown port's frames out on another port");
        map.ToAgwPort(0).Should().BeNull("ports count from 1");
        map.FromAgwPort(0).Should().Be(1);
        map.FromAgwPort(2).Should().Be(3);
        map.FromAgwPort(3).Should().BeNull();
        map.FromAgwPortUnbounded(9).Should().Be(10, "a stored byte keeps naming the byte it named");
        map.IsXrouter.Should().BeFalse();
        map.Numbers.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void APlainHostWithNoList_IsNotBounded()
    {
        var map = NodePortMap.Plain();
        map.ToAgwPort(9).Should().Be((byte)8);
        map.FromAgwPort(8).Should().Be(9);
        map.Numbers.Should().BeNull();
    }

    [Fact]
    public void Xrouter_ByteIsThePositionAmongThePortNumbers_GapsAndAll()
    {
        // PORT=19, PORT=2 and PORT=7, whatever order XROUTER.CFG writes
        // them in: probed on 505p, AGW counts 2 as 0, 7 as 1, 19 as 2.
        var map = NodePortMap.Xrouter([2, 7, 19]);
        map.ToAgwPort(2).Should().Be((byte)0);
        map.ToAgwPort(7).Should().Be((byte)1);
        map.ToAgwPort(19).Should().Be((byte)2);
        map.ToAgwPort(3).Should().BeNull("XRouter has no PORT=3; +1 would have called it (#370)");
        map.FromAgwPort(2).Should().Be(19);
        map.FromAgwPort(3).Should().BeNull();
        map.FromAgwPortUnbounded(3).Should().BeNull();
        map.IsXrouter.Should().BeTrue();
    }

    // ── 'G' answers ───────────────────────────────────────────────

    [Fact]
    public void OnlyXroutersOwnPortList_GivesACount()
    {
        NodePortNumbers.XrouterAgwPortCount(Bytes("3;Port1 with \"Two port\";Port2 with \"Seven port\";Port3 with \"Nineteen port\";\0")).Should().Be(3);
        NodePortNumbers.XrouterAgwPortCount(Bytes("1;Port1 with \"Loopback port\";\0")).Should().Be(1);
        NodePortNumbers.XrouterAgwPortCount(Bytes("2;Port1 Telnet;Port2 AXIP;\0")).Should().BeNull("BPQ");
        NodePortNumbers.XrouterAgwPortCount(Bytes("3;1 with Telnet;2 with A four;3 with A two;\0")).Should().BeNull("linbpq's fork");
        NodePortNumbers.XrouterAgwPortCount(Bytes("1;Port1 first soundcard mono;\0")).Should().BeNull("Dire Wolf");
        NodePortNumbers.XrouterAgwPortCount(Bytes("2;Port1 with \"Alpha\";Port3 with \"Delta\";\0")).Should().BeNull("not in order");
        NodePortNumbers.XrouterAgwPortCount(null).Should().BeNull();
    }

    [Fact]
    public void HiddenBpqPorts_AreWarnedAbout()
    {
        // BPQ counts hidden ports but leaves them out of the list.
        NodePortNumbers.HiddenPortsWarning(Bytes("3;Port1 Telnet;Port3 AXIP;\0")).Should().Contain("counts 3 ports over AGW but lists 2")
            .And.Contain("1 to N");
        NodePortNumbers.HiddenPortsWarning(Bytes("2;Port1 Telnet;Port2 AXIP;\0")).Should().BeNull();
        NodePortNumbers.HiddenPortsWarning(Bytes("Port1 Telnet;\0")).Should().BeNull("no count to compare");
    }

    [Fact]
    public void ThePortList_ShowsEachPortsDescriptionWithoutTheHostsPositionLabel()
    {
        AgwPortQuery.Describe("Port3 with \"Nineteen port\"").Should().Be("Nineteen port");
        AgwPortQuery.Describe("2 with A four").Should().Be("A four");
        AgwPortQuery.Describe("Port1 Telnet").Should().Be("Port1 Telnet");
    }

    [Fact]
    public void DireWolfWithAChannelGap_NumbersPortsByTheirLabels()
    {
        // Mono soundcards on channels 0 and 2 (direwolf server.c): two
        // ports, labelled Port1 and Port3, AGW bytes 0 and 2.
        var payload = Bytes("2;Port1 first soundcard mono;Port3 second soundcard mono;\0");
        var map = NodePortNumbers.Plain(payload);

        (map.ToAgwPort(1), map.ToAgwPort(3), map.ToAgwPort(2)).Should().Be(((byte?)0, (byte?)2, (byte?)null));
        (map.FromAgwPort(0), map.FromAgwPort(2), map.FromAgwPort(1)).Should().Be((1, 3, (int?)null));
        map.Numbers.Should().Equal(1, 3);
        NodePortNumbers.HiddenPortsWarning(payload).Should().BeNull("Dire Wolf counts the ports it lists");
        AgwPortQuery.NumberPorts(payload, map).Select(p => (p.Number, p.Index, p.Description))
            .Should().Equal((1, 0, "Port1 first soundcard mono"), (3, 2, "Port3 second soundcard mono"));
    }

    [Theory]
    [InlineData("3;Port1 Telnet;Port2 VHF;Port3 HF;\0")]          // BPQ
    [InlineData("3;1 with Telnet;2 with VHF;3 with HF;\0")]       // linbpq's own build
    [InlineData("3;Telnet;VHF;HF;\0")]                            // no labels: counted from 1
    public void Bpq_WithNoHiddenPorts_IsNumberedOneToN_AsBefore(string g)
    {
        var map = NodePortNumbers.Plain(Bytes(g));
        map.Numbers.Should().Equal(1, 2, 3);
        (map.ToAgwPort(1), map.ToAgwPort(3), map.ToAgwPort(4)).Should().Be(((byte?)0, (byte?)2, (byte?)null));
        AgwPortQuery.NumberPorts(Bytes(g), map).Select(p => (p.Number, p.Index)).Should().Equal((1, 0), (2, 1), (3, 2));
    }

    [Fact]
    public void Bpq_WithAHiddenPort_UsesNoByteBeyondTheListedPorts()
    {
        // linbpq: three ports, the second hidden. Its 'C' takes the byte as
        // a position among the visible ports and connects one past them on
        // its first port (M0LTE/linbpq#78).
        var payload = Bytes("3;1 with Telnet;3 with AXIP three;\0");
        var map = NodePortNumbers.Plain(payload);
        map.Numbers.Should().Equal(1);
        map.ToAgwPort(3).Should().BeNull();
        NodePortNumbers.HiddenPortsWarning(payload).Should().Contain("counts 3 ports over AGW but lists 2");
        AgwPortQuery.NumberPorts(payload, map).Select(p => p.Number).Should().Equal(1, null);
    }

    [Theory]
    [InlineData("4;1 with A;3 with C;4 with D;\0", new[] { 1 }, "won't use ports 3, 4")]   // slot 2 hidden
    [InlineData("3;1 with A;2 with B;\0", new[] { 1, 2 }, "")]                            // slot 3 hidden, after them
    public void Bpq_WithAHiddenPort_UsesOnlyPortsWhoseCallsAndBeaconsAgree(string g, int[] usable, string named)
    {
        // Slots 1 to 4, slot 2 hidden: 'C' would put byte 2 on slot 4
        // (a position among visible ports), 'M' and heard frames on slot 3
        // (M0LTE/linbpq#78). Only a port whose label is its position agrees.
        var payload = Bytes(g);
        var map = NodePortNumbers.Plain(payload);
        map.Numbers.Should().Equal(usable);
        foreach (var n in new[] { 1, 2, 3, 4 }.Except(usable)) map.ToAgwPort(n).Should().BeNull($"port {n} is refused");
        var warning = NodePortNumbers.HiddenPortsWarning(payload);
        warning.Should().Contain("1 to N");
        if (named.Length > 0) warning.Should().Contain(named);
        else warning.Should().NotContain("won't use");
    }

    [Fact]
    public void AStoredDireWolfByte_ConvertsToItsLabel_AndIsUsable()
    {
        var map = NodePortNumbers.Plain(Bytes("2;Port1 first soundcard mono;Port3 second soundcard mono;\0"));
        var number = map.FromAgwPortUnbounded(2);
        number.Should().Be(3);
        map.ToAgwPort(number!.Value).Should().Be((byte)2, "channel 2's 0.52 byte goes out on channel 2 again");
    }

    // ── Reading XRouter's numbers ─────────────────────────────────

    [Fact]
    public async Task XroutersNumbers_AreReadFromItsRhpv2Port_BindingEachNumberInTurn()
    {
        await using var server = Rhpv2UiFrameSenderTests.Xrouter(2, 7, 19);
        await using var rhp = await RhpClient.ConnectAsync(server.Endpoint.Address.ToString(), server.Endpoint.Port, TestContext.Current.CancellationToken);

        var numbers = await NodePortNumbers.RhpPortNumbersAsync(rhp, Local, 3, TestContext.Current.CancellationToken);

        numbers.Should().Equal(2, 7, 19);
        server.ReceivedFrames.OfType<BindMessage>().Select(b => b.Port).Should().Equal(
            Enumerable.Range(1, 19).Select(n => n.ToString()), "it stops once it has as many as AGW lists");
        server.ReceivedFrames.Should().NotContain(f => f is SendToMessage, "a bind sends nothing");
    }

    [Fact]
    public async Task OnAnXrouterLink_TheMapComesFromItsRhpv2Port_OncePerLink()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = Rhpv2UiFrameSenderTests.Xrouter(2, 7, 19);
        var numbers = Numbers(server);
        using var host = new FakeAgwHost();
        await using var link = await OpenLinkAsync(host, Xrouter3, ct);

        numbers.TryGet(link).Should().BeNull("an XRouter's numbers aren't known until they are read");
        var map = await numbers.GetAsync(link, ct);

        map!.IsXrouter.Should().BeTrue();
        map.Numbers.Should().Equal(2, 7, 19);
        map.ToAgwPort(19).Should().Be((byte)2);
        var binds = server.ReceivedFrames.OfType<BindMessage>().Count();
        (await numbers.GetAsync(link, ct)).Should().BeSameAs(map);
        numbers.TryGet(link).Should().BeSameAs(map);
        server.ReceivedFrames.OfType<BindMessage>().Count().Should().Be(binds, "read once per AGW connection");
        numbers.Warning.Should().BeNull();
    }

    [Fact]
    public async Task ADuplicateSocketAnswer_IsAPort()
    {
        // Probed on 505p: a bind to a number that is no port answers "Invalid
        // port" even while another socket holds our callsign on a real one,
        // so XRouter checks the port first; "Duplicate socket" means the
        // port exists and a beacon holds it now.
        await using var server = new MockRhpServer
        {
            Handler = m => m is BindMessage b
                ? b.Port == "7" ? new BindReplyMessage { Handle = b.Handle, ErrCode = 9, ErrText = "Duplicate socket" }
                : int.TryParse(b.Port, out var n) && n is 2 or 19 ? null
                : new BindReplyMessage { Handle = b.Handle, ErrCode = 10, ErrText = "Invalid port" }
                : null,
        };
        server.Start();
        await using var rhp = await RhpClient.ConnectAsync(server.Endpoint.Address.ToString(), server.Endpoint.Port, TestContext.Current.CancellationToken);

        (await NodePortNumbers.RhpPortNumbersAsync(rhp, Local, 3, TestContext.Current.CancellationToken)).Should().Equal(2, 7, 19);
    }

    [Fact]
    public async Task ANodeThatDoesntAnswerThePortQuery_HasNoMap_UntilItDoes()
    {
        // A slow first answer from an XRouter must not leave a plain map
        // (byte = number - 1) for the whole connection (#370).
        var ct = TestContext.Current.CancellationToken;
        var numbers = new NodePortNumbers(Rhpv2UiFrameSenderTests.Options("127.0.0.1", 1), NullLogger<NodePortNumbers>.Instance);
        using var host = new FakeAgwHost();
        var answer = false;
        await using var link = await OpenLinkAsync(host, Bytes("2;Port1 Telnet;Port2 VHF;\0"), ct, () => answer);

        (await numbers.GetAsync(link, ct)).Should().BeNull("no answer within the port query's time: nothing is guessed");
        numbers.TryGet(link).Should().BeNull();
        numbers.Warning.Should().Contain("did not answer");

        answer = true;
        (await numbers.GetAsync(link, ct))!.Numbers.Should().Equal(1, 2);
    }

    [Fact]
    public async Task AnXrouterWhoseRhpv2PortsDontMatch_HasNoMap_AndSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = Rhpv2UiFrameSenderTests.Xrouter(2, 7);
        var numbers = Numbers(server);
        using var host = new FakeAgwHost();
        await using var link = await OpenLinkAsync(host, Xrouter3, ct);

        (await numbers.GetAsync(link, ct)).Should().BeNull("nothing may be sent on a guessed port");
        numbers.Warning.Should().Contain("PORT=");
        numbers.TryGet(link).Should().BeNull();
    }

    [Fact]
    public async Task AnXrouterWithNoRhpv2Port_HasNoMap()
    {
        var ct = TestContext.Current.CancellationToken;
        var unused = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        unused.Start();
        var port = ((System.Net.IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var numbers = new NodePortNumbers(Rhpv2UiFrameSenderTests.Options("127.0.0.1", port), NullLogger<NodePortNumbers>.Instance);
        using var host = new FakeAgwHost();
        await using var link = await OpenLinkAsync(host, Xrouter3, ct);

        (await numbers.GetAsync(link, ct)).Should().BeNull();
    }

    [Fact]
    public async Task OnABpqLink_PortsCountFromOne_BoundedByItsList_AndHiddenPortsAreWarnedAbout()
    {
        var ct = TestContext.Current.CancellationToken;
        var numbers = new NodePortNumbers(Rhpv2UiFrameSenderTests.Options("127.0.0.1", 1), NullLogger<NodePortNumbers>.Instance);
        using var host = new FakeAgwHost();
        await using var link = await OpenLinkAsync(host, Bytes("3;Port1 Telnet;Port2 VHF;\0"), ct);

        var map = await numbers.GetAsync(link, ct);
        (map!.IsXrouter, map.ToAgwPort(2), map.ToAgwPort(3)).Should().Be((false, (byte?)1, (byte?)null));
        numbers.Warning.Should().Contain("lists 2");
        numbers.TryGet(link).Should().BeSameAs(map);
    }

    /// <summary>XRouter's answer to a 'G' for three ports.</summary>
    private static readonly byte[] Xrouter3 =
        Bytes("3;Port1 with \"Two port\";Port2 with \"Seven port\";Port3 with \"Nineteen port\";\0");

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static NodePortNumbers Numbers(MockRhpServer server) =>
        new(Rhpv2UiFrameSenderTests.Options(server.Endpoint.Address.ToString(), server.Endpoint.Port), NullLogger<NodePortNumbers>.Instance);

    /// <summary>A registered AGW link to a fake node that answers the
    /// port query with <paramref name="ports"/>.</summary>
    private static async Task<AgwLink> OpenLinkAsync(FakeAgwHost host, byte[] ports, CancellationToken ct, Func<bool>? answer = null)
    {
        var opening = AgwLink.OpenAsync("127.0.0.1", host.Port, Local, _ => { }, NullLogger.Instance, ct: ct);
        var node = await host.AcceptAsync(ct);
        await node.ExpectRegisterAsync(ct);
        await node.WriteAsync(ct, FakeAgwSocket.RegisterReply(0x01));
        await node.ReadUntilAsync('m', ct);
        var link = await opening;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var f = await node.ReadFrameAsync(ct);
                    if (f.Kind == 'G' && (answer is null || answer())) await node.WriteAsync(ct, new AgwFrame(0, 'G', 0, "", "", ports));
                }
            }
            catch { /* the link closed */ }
        }, ct);
        return link;
    }
}
