using AwesomeAssertions;
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
