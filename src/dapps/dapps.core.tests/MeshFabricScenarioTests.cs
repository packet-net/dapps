using AwesomeAssertions;
using dapps.meshcore;
using dapps.meshcore.sim;
using Xunit;

namespace dapps.core.tests;

/// <summary>
/// Full-stack scenarios: the REAL MeshCore bearer (transport + reliability ACK/resend +
/// idempotent inbound + passive discovery) running on many nodes over a multi-hop
/// virtual mesh - the coverage two directly-adjacent bench radios can't give. No RF, no
/// loss (so it's fast + deterministic); loss-recovery timing is left to a longer harness
/// run since reliability resends are on a multi-second cadence.
/// </summary>
public sealed class MeshFabricScenarioTests
{
    private static async Task WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task FullStack_FourRelayHops_BidirectionalDelivery_Discovery_NoDuplicates()
    {
        // A off R1 ... R4 off C: the two DAPPS nodes are four relay hops apart.
        var f = new MeshFabric();
        f.AddRelay("R1"); f.AddRelay("R2"); f.AddRelay("R3"); f.AddRelay("R4");
        f.ConnectChain(["R1", "R2", "R3", "R4"]);
        var a = new MeshDappsNode(f, "GB7A-1");
        var c = new MeshDappsNode(f, "GB7C-1");
        f.Connect("GB7A-1", "R1");
        f.Connect("GB7C-1", "R4");

        using var cts = new CancellationTokenSource();
        var loops = Task.WhenAll(a.RunAsync(cts.Token), c.RunAsync(cts.Token));

        const int N = 4;
        for (var i = 0; i < N; i++)
        {
            await a.SendAsync("GB7C-1", i, $"A->C #{i}", cts.Token);
            await c.SendAsync("GB7A-1", i, $"C->A #{i}", cts.Token);
        }

        await WaitUntil(() => a.DistinctSeqs == N && c.DistinctSeqs == N, TimeSpan.FromSeconds(15));
        cts.Cancel();
        try { await loops; } catch { /* cancelled */ }

        c.DistinctSeqs.Should().Be(N, "every message reached C across four relay hops");
        a.DistinctSeqs.Should().Be(N);
        c.Delivered.Should().Be(N, "idempotent: flooding + dedup never double-delivers to the app");
        a.Delivered.Should().Be(N);
        c.DiscoveredPeers.Should().Contain("GB7A-1", "C learned A purely by hearing its traffic, multi-hop");
        a.DiscoveredPeers.Should().Contain("GB7C-1");
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.4)]
    public async Task LossyMultiHop_ReliabilityRecoversEveryMessage_ExactlyOnce(double lossPerEdge)
    {
        // A - R1 - R2 - R3 - B over FOUR lossy hops. End-to-end reliability (ACK + resend)
        // must recover every message despite heavy per-edge loss, and idempotent inbound
        // must still deliver each exactly once - a lost ACK makes the sender resend, so B
        // sees duplicates it must dedup. This is the whole reason the reliability layer
        // exists, and multi-hop compounds the loss; the earlier tests ran at 0% loss.
        var f = new MeshFabric(seed: 20260701);
        f.AddRelay("R1"); f.AddRelay("R2"); f.AddRelay("R3");

        // Accelerate the resend timings so this runs in CI-time rather than on the 20 s
        // production backoff (WaitUntil returns as soon as everything arrives, so the
        // common case finishes in a few seconds; the deadline is only a safety cap).
        var fast = new MeshCoreReliability.Options(
            BaseBackoff: TimeSpan.FromMilliseconds(120), Multiplier: 1.4,
            MaxBackoff: TimeSpan.FromMilliseconds(600), MaxLifetime: TimeSpan.FromSeconds(60));
        var poll = TimeSpan.FromMilliseconds(100);

        var a = new MeshDappsNode(f, "GB7A-1", reliabilityOptions: fast, resendPoll: poll);
        var b = new MeshDappsNode(f, "GB7B-1", reliabilityOptions: fast, resendPoll: poll);
        f.Connect("GB7A-1", "R1", lossPerEdge);
        f.Connect("R1", "R2", lossPerEdge);
        f.Connect("R2", "R3", lossPerEdge);
        f.Connect("R3", "GB7B-1", lossPerEdge);

        using var cts = new CancellationTokenSource();
        var loops = Task.WhenAll(a.RunAsync(cts.Token), b.RunAsync(cts.Token));

        const int N = 5;
        for (var i = 0; i < N; i++)
            await a.SendAsync("GB7B-1", i, $"A->B #{i}", cts.Token);

        await WaitUntil(() => b.DistinctSeqs == N, TimeSpan.FromSeconds(50));
        cts.Cancel();
        try { await loops; } catch { /* cancelled */ }

        b.DistinctSeqs.Should().Be(N, "reliability resends recovered every message despite {0:P0} per-hop loss", lossPerEdge);
        b.Delivered.Should().Be(N, "idempotent: each delivered exactly once despite resends after lost ACKs");
        f.Dropped.Should().BeGreaterThan(0, "loss was genuinely exercised (not a no-op path)");
    }

    [Fact]
    public async Task FullStack_FanIn_ManySendersOneCollector_AllDeliveredOnce()
    {
        // Five DAPPS senders hang off a relay backbone; all send to one collector. Proves
        // many nodes + concurrent multi-hop floods still deliver each message exactly once.
        var f = new MeshFabric();
        var relays = new[] { "R0", "R1", "R2", "R3", "R4", "R5" };
        foreach (var r in relays) f.AddRelay(r);
        f.ConnectChain(relays);

        var collector = new MeshDappsNode(f, "GB7COL-1");
        f.Connect("GB7COL-1", "R0");

        var senders = new List<MeshDappsNode>();
        for (var i = 0; i < 5; i++)
        {
            var s = new MeshDappsNode(f, $"GB7S{i}-1");
            f.Connect(s.Callsign, relays[i + 1]);   // each sender off a different relay
            senders.Add(s);
        }

        using var cts = new CancellationTokenSource();
        var loops = Task.WhenAll(new[] { collector.RunAsync(cts.Token) }
            .Concat(senders.Select(s => s.RunAsync(cts.Token))));

        const int PerSender = 3;
        foreach (var s in senders)
            for (var i = 0; i < PerSender; i++)
                await s.SendAsync("GB7COL-1", i, $"{s.Callsign} #{i}", cts.Token);

        var expected = senders.Count * PerSender;   // seq values collide across senders, so count deliveries
        await WaitUntil(() => collector.Delivered >= expected, TimeSpan.FromSeconds(20));
        cts.Cancel();
        try { await loops; } catch { /* cancelled */ }

        collector.Delivered.Should().Be(expected, "each of the 15 messages delivered exactly once (no duplicates)");
        collector.DiscoveredPeers.Should().Contain(senders.Select(s => s.Callsign),
            "the collector discovered every sender it heard");
    }
}
