using System.Collections.Concurrent;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// <see cref="NodeDiagnostics"/> (#425) against a scripted node prompt:
/// what it logs at start-up for each Connect via, and the LINKS snapshot
/// when a link is busy, rate-limited and never holding up the caller.
/// </summary>
public sealed class NodeDiagnosticsTests
{
    private const string Us = "G9APP-3";

    private readonly CapturingLoggerFactory logs = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-30T12:00:00Z"));

    private sealed class ScriptedPrompt(Func<string, Task<string>> answer) : INodePrompt
    {
        public ConcurrentQueue<string> Asked { get; } = new();

        public Task<string> AskAsync(string command, CancellationToken ct)
        {
            Asked.Enqueue(command);
            return answer(command);
        }
    }

    private static ScriptedPrompt Answering(string nodes, string links) =>
        new(c => Task.FromResult(c == "NODES" ? nodes : links));

    private NodeDiagnostics Make(INodePrompt prompt, params string[] vias) =>
        new(prompt, _ => Task.FromResult<IReadOnlyCollection<string>>(vias),
            new StaticOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = Us, NodeBearer = "rhpv2" }),
            new Logger<NodeDiagnostics>(logs), agwLinks: null, clock)
        {
            StartupDelay = TimeSpan.Zero,
        };

    private static BackhaulRoute Via(string peer, string via) =>
        new(Callsign: peer, BearerPort: 2, UdpEndpoint: null, ConnectScript: null, MeshCoreChannel: null, ConnectVia: via);

    private async Task StartupAsync(NodeDiagnostics diag, CancellationToken ct)
    {
        await diag.StartAsync(ct);
        await diag.ExecuteTask!.WaitAsync(FakeAgwHost.FrameTimeout, ct);
    }

    private IReadOnlyList<string> Lines => [.. logs.Entries.Where(e => e.Category.EndsWith(nameof(NodeDiagnostics), StringComparison.Ordinal)).Select(e => e.Message)];

    [Fact]
    public async Task AtStartup_EachViaGetsOneLine_SayingWhetherItIsInTheNodesList()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = Answering(NodeTableTextTests.BpqNodes, "");
        var diag = Make(prompt, "CAL", "CALNE");

        await StartupAsync(diag, ct);

        prompt.Asked.Should().Equal("NODES");
        Lines.Should().Equal(
            "CAL is in the node's NODES list as CAL:G9CAL; sessions through it can go by NET/ROM",
            "CALNE isn't in the node's NODES list; sessions through it go by L2, one at a time");
    }

    [Fact]
    public async Task AtStartup_WithNoVias_ItSaysSoOnce_AndAsksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = Answering(NodeTableTextTests.BpqNodes, "");

        await StartupAsync(Make(prompt), ct);

        prompt.Asked.Should().BeEmpty();
        Lines.Should().Equal("Node check: no neighbour has a Connect via, so there is nothing to ask the node");
    }

    [Fact]
    public async Task AtStartup_ANodeThatCantBeAsked_GivesOneLineSayingWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = new ScriptedPrompt(_ => throw new NodePromptUnavailableException(
            "the node's RHPv2 has no way to its prompt (pdn has none: RHP error 16: family 'unix' is not implemented yet (ax25 only))"));

        await StartupAsync(Make(prompt, "CALNE"), ct);

        Lines.Should().Equal(
            "Node check: can't ask the node about Connect via nodes: the node's RHPv2 has no way to its prompt (pdn has none: RHP error 16: family 'unix' is not implemented yet (ax25 only)); type NODES at the node's console to check");
    }

    [Fact]
    public async Task ALinkBusy_LogsTheLinksLinesForIt_ByTheViasNodeCallToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = Answering(NodeTableTextTests.BpqNodes, NodeTableTextTests.BpqLinks);
        var diag = Make(prompt, "CAL");
        await StartupAsync(diag, ct);

        diag.LinkBusy(Via("G9PRA-3", "CAL"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        Lines.Should().EndWith("Node LINKS while the link to CAL is busy: G9CAL G9LOC S=5 P=2 T=3 V=2.2 Q=0");
    }

    [Fact]
    public async Task ALinkBusy_WithNoLineForIt_SaysSoInOneLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = Answering(NodeTableTextTests.BpqNodes, NodeTableTextTests.BpqLinks);
        var diag = Make(prompt);

        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        Lines.Should().Equal("Node LINKS while the link to CALNE is busy: no line for CALNE or G9APP-3");
    }

    [Fact]
    public async Task ALinkBusy_IsSnapshottedAtMostOnceInTenMinutes_PerLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = Answering("", NodeTableTextTests.BpqLinks);
        var diag = Make(prompt);

        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        diag.LinkBusy(Via("GB7LOX-3", "CALNE"), Us);
        clock.Advance(TimeSpan.FromMinutes(9));
        diag.LinkBusy(Via("EI7KX-3", "CALNE"), Us);
        diag.LinkBusy(Via("G9PRA-3", "G9PRA-3"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        prompt.Asked.Should().HaveCount(2, "one for CALNE, one for G9PRA-3, a link of its own");

        clock.Advance(TimeSpan.FromMinutes(1));
        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        prompt.Asked.Should().HaveCount(3, "ten minutes on, CALNE is asked about again");
    }

    [Fact]
    public async Task ALinkBusy_ReturnsAtOnce_WhileTheNodeIsSlowToAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new ScriptedPrompt(_ => answer.Task);
        var diag = Make(prompt);

        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);

        diag.Snapshots.Should().ContainSingle().Which.IsCompleted.Should().BeFalse("the forwarder doesn't wait for the node");
        answer.SetResult(NodeTableTextTests.BpqLinks);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);
    }

    [Fact]
    public async Task ALinkBusy_WhenTheNodeCantBeAsked_GivesOneLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = new ScriptedPrompt(_ => throw new NodePromptUnavailableException("the AGW connection to the node isn't open"));
        var diag = Make(prompt);

        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        Lines.Should().Equal("Node LINKS while the link to CALNE is busy: can't ask the node: the AGW connection to the node isn't open");
    }

    [Fact]
    public async Task AtStartup_ANodeThatSaysNothing_IsNotTakenAsAnEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = Answering("", "");

        await StartupAsync(Make(prompt, "CAL", "CALNE"), ct);

        Lines.Should().Equal("Node check: the node didn't answer NODES; type NODES at the node's console to check");
    }

    [Fact]
    public async Task ALinkBusy_WhenTheNodeSaysNothing_SaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var diag = Make(Answering("", " \r"));

        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        Lines.Should().Equal("Node LINKS while the link to CALNE is busy: the node didn't answer LINKS");
    }

    [Fact]
    public async Task ANodeWithNoWayToItsPrompt_IsNotAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = new ScriptedPrompt(_ => throw new NodePromptUnavailableException("the node's RHPv2 has no way to its prompt", forGood: true));
        var diag = Make(prompt, "CALNE");
        await StartupAsync(diag, ct);

        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        clock.Advance(TimeSpan.FromMinutes(10));
        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        prompt.Asked.Should().Equal("NODES");
        Lines.Should().ContainSingle();
    }

    [Fact]
    public async Task ANodeThatCantBeAskedForNow_IsAskedAgainLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = new ScriptedPrompt(_ => throw new NodePromptUnavailableException("the AGW connection to the node isn't open"));
        var diag = Make(prompt);

        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        clock.Advance(TimeSpan.FromMinutes(10));
        diag.LinkBusy(Via("GB7AGM-3", "CALNE"), Us);
        await Task.WhenAll(diag.Snapshots).WaitAsync(FakeAgwHost.FrameTimeout, ct);

        prompt.Asked.Should().HaveCount(2);
        diag.Snapshots.Count.Should().BeLessThanOrEqualTo(1, "finished snapshots aren't kept");
    }

    [Fact]
    public void DistinctVias_AreUpperCasedAndDeduplicated_LeavingOutAViaThatIsTheNeighbourItself()
    {
        NodeDiagnostics.DistinctVias([
            new DbNeighbour { Callsign = "GB7AGM-3", ConnectVia = "calne" },
            new DbNeighbour { Callsign = "GB7LOX-3", ConnectVia = "CALNE " },
            new DbNeighbour { Callsign = "EI7KX-3", ConnectVia = "G4XYZ-7" },
            new DbNeighbour { Callsign = "G9PRA-3", ConnectVia = "G9PRA-3" },
            new DbNeighbour { Callsign = "G9PRB-3" },
        ]).Should().Equal("CALNE", "G4XYZ-7");
    }
}
