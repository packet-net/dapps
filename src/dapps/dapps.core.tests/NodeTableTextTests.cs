using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

/// <summary>
/// <see cref="NodeTableText"/> on what real nodes printed, captured from
/// #425's Rig A (LinBPQ 6.0.25.41 and XRouter 505p, four nodes LOCAL,
/// CALNE, PEER1 and PEER2 with NET/ROM between them).
/// </summary>
public sealed class NodeTableTextTests
{
    /// <summary>LinBPQ LOCAL's NODES, over an AGW session to SWITCH.</summary>
    internal const string BpqNodes =
        "LOC:G9LOC} Nodes\rCAL:G9CAL           PRA:G9PRA           PRANR:G9PRA-5       PRB:G9PRB           \rPRBNR:G9PRB-5       \r";

    /// <summary>LinBPQ CALNE's NODES.</summary>
    internal const string BpqCalneNodes =
        "CAL:G9CAL} Nodes\rLOC:G9LOC           PRA:G9PRA           PRANR:G9PRA-5       PRB:G9PRB           \rPRBNR:G9PRB-5       \r";

    /// <summary>LinBPQ before NET/ROM has learnt anything.</summary>
    internal const string BpqNodesEmpty = "LOC:G9LOC} Nodes\r";

    /// <summary>XRouter LOCAL's NODES over an RHPv2 'unix' session,
    /// greeting and all.</summary>
    internal const string XrouterNodes =
        "Connected to SWITCH\r\rG9LOC:LOC} Nodes:\r\r    CAL:G9CAL      CALCHT:G9CAL-8    LOCCHT:G9LOC-8       PRA:G9PRA     \r PRACHT:G9PRA-8       PRB:G9PRB      PRBCHT:G9PRB-8   \r\r7 nodes found\r";

    /// <summary>LinBPQ LOCAL's LINKS with its node link to CALNE and an
    /// application's L2 link (G9TST-9 to CALNE's G9CAL-3) up.</summary>
    internal const string BpqLinks =
        "LOC:G9LOC} Links\rG9CAL     G9LOC      S=5 P=2 T=3 V=2.2 Q=0\rG9CAL-3   G9TST-9    S=5 P=2 T=2 V=2 Q=0\r";

    /// <summary>XRouter CALNE's LINKS.</summary>
    internal const string XrouterLinks =
        "Connected to SWITCH\r\rG9CAL:CAL} AX25 L2 Links:\r\r Remote     Local      Prt  Sta  V  Try   T3  Pac  Max  Idle  Txq  Rxq  Id\r G9LOC      G9CAL        1    5  2    0  166  256    3   205    0    0  1\r G9PRA      G9CAL        2    5  2    0  192  256    3   195    0    0  2\r G9PRB      G9CAL        3    5  2    0   45  256    3   149    0    0  3\r(End of list)\r";

    [Fact]
    public void LinbpqNodes_AreReadFourToALine_WithoutThePromptHeader()
    {
        NodeTableText.ParseNodes(BpqNodes).Select(n => n.ToString())
            .Should().Equal("CAL:G9CAL", "PRA:G9PRA", "PRANR:G9PRA-5", "PRB:G9PRB", "PRBNR:G9PRB-5");
        NodeTableText.ParseNodes(BpqCalneNodes).Select(n => n.Callsign)
            .Should().Equal("G9LOC", "G9PRA", "G9PRA-5", "G9PRB", "G9PRB-5");
    }

    [Fact]
    public void XrouterNodes_AreRead_WithoutTheGreetingHeaderOrCount()
    {
        NodeTableText.ParseNodes(XrouterNodes).Select(n => n.ToString()).Should().Equal(
            "CAL:G9CAL", "CALCHT:G9CAL-8", "LOCCHT:G9LOC-8", "PRA:G9PRA", "PRACHT:G9PRA-8", "PRB:G9PRB", "PRBCHT:G9PRB-8");
    }

    [Fact]
    public void AnEmptyList_HasNoEntries() => NodeTableText.ParseNodes(BpqNodesEmpty).Should().BeEmpty();

    [Fact]
    public void ANodeWithNoAlias_IsReadByItsCallAlone()
    {
        // LinBPQ prints such a node as its call alone, in the same 20-wide column.
        var nodes = NodeTableText.ParseNodes("LOC:G9LOC} Nodes\rG9XYZ-2             CAL:G9CAL           \r");
        nodes.Should().Equal(new NodeListEntry(null, "G9XYZ-2"), new NodeListEntry("CAL", "G9CAL"));
        nodes[0].ToString().Should().Be("G9XYZ-2");
    }

    [Theory]
    [InlineData("CAL", "CAL:G9CAL")]
    [InlineData("cal", "CAL:G9CAL")]
    [InlineData("G9CAL", "CAL:G9CAL")]
    [InlineData("G9CAL-0", "CAL:G9CAL")]
    [InlineData("PRANR", "PRANR:G9PRA-5")]
    [InlineData("G9PRA-5", "PRANR:G9PRA-5")]
    public void AVia_IsFoundByAliasOrCall(string via, string expected) =>
        NodeTableText.Find(NodeTableText.ParseNodes(BpqNodes), via)!.ToString().Should().Be(expected);

    [Theory]
    [InlineData("CALNE")]
    [InlineData("G9CAL-3")]
    [InlineData("G9LOC")]
    public void AViaNotListed_IsNotFound(string via) =>
        NodeTableText.Find(NodeTableText.ParseNodes(BpqNodes), via).Should().BeNull();

    [Fact]
    public void LinbpqLinks_TheLinesNamingTheCalls_AreKept()
    {
        NodeTableText.LinesNaming(BpqLinks, ["G9CAL-3", "G9TST-9"])
            .Should().Equal("G9CAL-3 G9TST-9 S=5 P=2 T=2 V=2 Q=0");
        NodeTableText.LinesNaming(BpqLinks, ["G9CAL"])
            .Should().Equal("G9CAL G9LOC S=5 P=2 T=3 V=2.2 Q=0");
    }

    [Fact]
    public void XrouterLinks_TheLinesNamingTheCalls_AreKept_NotTheHeader()
    {
        NodeTableText.LinesNaming(XrouterLinks, ["G9PRA", "G9APP-3"])
            .Should().Equal("G9PRA G9CAL 2 5 2 0 192 256 3 195 0 0 2");
        NodeTableText.LinesNaming(XrouterLinks, ["G9CAL"]).Should().HaveCount(3,
            "every link at CALNE names it as Local; the header's G9CAL:CAL} is not a word for it");
    }

    [Fact]
    public void Links_WithNoLineForTheCalls_GiveNothing() =>
        NodeTableText.LinesNaming(BpqLinks, ["CALNE", "MB7NPW-3"]).Should().BeEmpty();
}
