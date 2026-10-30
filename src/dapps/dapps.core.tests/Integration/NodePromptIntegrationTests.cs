using AwesomeAssertions;
using dapps.client.Transport.Agw;
using dapps.client.Tx;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests.Integration;

/// <summary>A TX gate that is shut: the node check must work all the same,
/// since nothing it does goes on air.</summary>
internal sealed class ShutTxGate : IDappsTxGate
{
    public bool TxAllowed => false;
    public string? BlockReason => "operator pressed master TX-stop";
}

/// <summary>
/// #425 on real LinBPQ: <see cref="NodePrompt"/> reaches the node's own
/// prompt by an AGW session to SWITCH on the daemon's one AGW connection,
/// with TX stopped, and the connection carries on as before.
/// </summary>
[Collection("Linbpq integration")]
[Trait("Category", "Integration")]
public sealed class LinbpqNodePromptIntegrationTests(LinbpqIntegrationFixture fixture)
{
    [Fact]
    public async Task NodesAndLinks_AreAnsweredAtTheNodesPrompt_WithTxStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var links = new StandaloneAgwLinkSource(fixture.Host, fixture.AgwPort, "G9NDG-3", NullLoggerFactory.Instance, new ShutTxGate());
        var link = await links.GetLinkAsync(TimeSpan.FromSeconds(10), ct);
        var prompt = new NodePrompt(
            new StaticOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "G9NDG-3", NodeBearer = "agw", NodeHost = fixture.Host, AgwPort = fixture.AgwPort }),
            links, NullLogger<NodePrompt>.Instance);

        var nodes = await prompt.AskAsync("NODES", ct);
        nodes.Should().Contain($"NODE:{fixture.LocalCallsign}}} Nodes");
        NodeTableText.ParseNodes(nodes).Should().BeEmpty("the fixture is one node on its own");

        var linksReply = await prompt.AskAsync("LINKS", ct);
        linksReply.Should().Contain($"NODE:{fixture.LocalCallsign}}} Links");

        links.TryGetLink().Should().BeSameAs(link, "the node sessions came and went on the one connection");
        (await link!.QueryPortsAsync(ct)).Payload.Should().NotBeEmpty();
    }
}

/// <summary>
/// #425 on real XRouter: <see cref="NodePrompt"/> reaches the node's own
/// prompt by an RHPv2 open in the unix family, whether DAPPS's bearer is
/// RHPv2 or AGW (told apart by its AGW port list).
/// </summary>
[Collection("XRouter integration")]
[Trait("Category", "Integration")]
public sealed class XrouterNodePromptIntegrationTests(XrouterIntegrationFixture fixture)
{
    [Fact]
    public async Task OnTheRhpv2Bearer_NodesIsAnsweredAtTheNodesPrompt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var links = new StandaloneAgwLinkSource(fixture.Host, fixture.AgwPort, "G9NDG-3", NullLoggerFactory.Instance);
        var prompt = new NodePrompt(
            new StaticOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "G9NDG-3", NodeBearer = "rhpv2", NodeHost = fixture.Host, RhpPort = fixture.RhpPort }),
            links, NullLogger<NodePrompt>.Instance);

        var nodes = await prompt.AskAsync("NODES", ct);

        nodes.Should().Contain("} Nodes:");
    }

    [Fact]
    public async Task OnTheAgwBearer_XrouterIsAskedOverRhpv2()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var links = new StandaloneAgwLinkSource(fixture.Host, fixture.AgwPort, "G9NDG-4", NullLoggerFactory.Instance, new ShutTxGate());
        await links.GetLinkAsync(TimeSpan.FromSeconds(10), ct);
        var prompt = new NodePrompt(
            new StaticOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "G9NDG-4", NodeBearer = "agw", NodeHost = fixture.Host, AgwPort = fixture.AgwPort, RhpPort = fixture.RhpPort }),
            links, NullLogger<NodePrompt>.Instance);

        var reply = await prompt.AskAsync("LINKS", ct);

        reply.Should().Contain("} AX25 L2 Links:");
    }
}

/// <summary>
/// #425 on real pdn: its RHPv2 has no way to its own prompt (it refuses
/// the unix family), so the node check says it can't ask, in one line.
/// </summary>
[Collection("pdn two-instance")]
[Trait("Category", "Integration")]
public sealed class PdnNodePromptIntegrationTests(TwoPdnFixture fixture)
{
    [Fact]
    public async Task Pdn_CantBeAsked_AndSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var links = new StandaloneAgwLinkSource(fixture.Host, 1, "G9NDG-3", NullLoggerFactory.Instance);
        var prompt = new NodePrompt(
            new StaticOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = fixture.ApplCallA, NodeBearer = "rhpv2", NodeHost = fixture.Host, RhpPort = fixture.RhpPortA }),
            links, NullLogger<NodePrompt>.Instance);

        var refused = await FluentActions.Awaiting(() => prompt.AskAsync("NODES", ct)).Should().ThrowAsync<NodePromptUnavailableException>();

        refused.Which.Message.Should().StartWith("the node's RHPv2 has no way to its prompt (pdn has none: ");
    }
}
