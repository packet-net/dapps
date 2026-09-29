namespace dapps.core.tests.Integration;

/// <summary>
/// The exchange between two DAPPS daemons on two packet.net nodes (pdn)
/// linked over AXUDP, each daemon attached over RHPv2, with pdn's frame
/// feed as the record of what went over the air (<see cref="TwoPdnFixture"/>):
///
///     app -> DAPPS A -RHPv2- pdn-A -AXUDP- pdn-B -RHPv2- DAPPS B -> app
///
/// The shared cases: a message pushed with the whole exchange, a burst on
/// one connection, mail going back on the caller's session, compressed,
/// split and binary payloads, a held link both ways and a quiet one closed,
/// an ordered stream, and a probe.
/// </summary>
[Collection("pdn two-instance")]
[Trait("Category", "Integration")]
public sealed class PdnEndToEndTests(TwoPdnFixture fixture) : DappsExchangeTests(fixture);

/// <summary>
/// Crossed calls between two DAPPS daemons on two pdn nodes over AXUDP:
/// both have mail for the other at once, so both dial. AXUDP has no
/// airtime, so the two calls cross only when they go out within a few
/// milliseconds of each other; the submits are up to 50 ms apart, which
/// gives both calls crossing on the wire and a call arriving just as the
/// other node's link comes up.
/// </summary>
[Collection("pdn two-instance")]
[Trait("Category", "Integration")]
public sealed class PdnCrossedCallAxudpTests(TwoPdnFixture fixture) : CrossedCallScenarioTests(fixture)
{
    protected override TimeSpan RoundLimit => TimeSpan.FromSeconds(30);
    protected override (int From, int To) StaggerMs => (0, 51);
    protected override bool SpotsCrossings => true;
}

/// <summary>
/// The exchange between DAPPS on a pdn node and DAPPS on a BPQ node, pdn's
/// AXUDP port facing BPQ's AXIP port (<see cref="PdnBpqFixture"/>):
///
///     app -> DAPPS A -RHPv2- pdn-A -AXUDP- BPQ-B -AGW- DAPPS B -> app
///
/// The same cases as <see cref="PdnEndToEndTests"/>, across the two stacks.
/// </summary>
[Collection("pdn and BPQ")]
[Trait("Category", "Integration")]
public sealed class PdnBpqEndToEndTests(PdnBpqFixture fixture) : DappsExchangeTests(fixture);

/// <summary>
/// <see cref="PdnBpqEndToEndTests"/> with the sides swapped: DAPPS on BPQ
/// is A, so it's BPQ that calls pdn.
/// </summary>
[Collection("pdn and BPQ")]
[Trait("Category", "Integration")]
public sealed class BpqPdnEndToEndTests(PdnBpqFixture fixture) : DappsExchangeTests(new SwappedPair(fixture));
