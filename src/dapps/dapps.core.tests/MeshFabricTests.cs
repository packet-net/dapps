using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.meshcore;
using dapps.meshcore.sim;
using Xunit;

namespace dapps.core.tests;

/// <summary>
/// The simulated multi-hop MeshCore fabric (#24 test-strategy follow-up): validates the
/// flood/dedup/hop/scope model, and that the REAL bearer transport carries a
/// BackhaulMessage across several relay hops and decodes it exactly once. Deterministic
/// and fast - no bearer poll loops, no RF. See MeshFabricScenarioTests for the full
/// bearer stack running over the fabric.
/// </summary>
public sealed class MeshFabricTests
{
    // How many app-deliveries a leaf's link received (each fabric Deliver enqueues one
    // ChannelData; a deduped duplicate never enqueues).
    private static int Received(SimulatedMeshCoreLink link)
    {
        var batch = link.DrainAsync(CancellationToken.None).GetAwaiter().GetResult();
        return batch?.Data.Count ?? 0;
    }

    [Fact]
    public void Chain_MultiHop_DeliversToFarLeaf()
    {
        // A(leaf) - R1 - R2 - R3 - B(leaf): a message from A must traverse 3 relay hops.
        var f = new MeshFabric();
        var a = f.AddLeaf("A");
        f.AddRelay("R1"); f.AddRelay("R2"); f.AddRelay("R3");
        var b = f.AddLeaf("B");
        f.ConnectChain(["A", "R1", "R2", "R3", "B"]);

        a.SendDataAsync("hello"u8.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

        Received(b).Should().Be(1, "the far leaf hears the flood exactly once");
    }

    [Fact]
    public void Flood_DedupsAcrossMultiplePaths()
    {
        // Diamond: A -> {R1, R2} -> B. B hears the same packet via both relays but the
        // dedup ring must deliver it only once.
        var f = new MeshFabric();
        var a = f.AddLeaf("A");
        f.AddRelay("R1"); f.AddRelay("R2");
        var b = f.AddLeaf("B");
        f.Connect("A", "R1"); f.Connect("A", "R2");
        f.Connect("R1", "B"); f.Connect("R2", "B");

        a.SendDataAsync("x"u8.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

        Received(b).Should().Be(1, "two paths, but the packet is deduped to a single delivery");
    }

    [Fact]
    public void Leaf_DoesNotRelay()
    {
        // A - B(leaf) - C: B is a LEAF so it must not re-flood; C never hears it.
        var f = new MeshFabric();
        var a = f.AddLeaf("A");
        var b = f.AddLeaf("B");
        var c = f.AddLeaf("C");
        f.ConnectChain(["A", "B", "C"]);

        a.SendDataAsync("y"u8.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

        Received(b).Should().Be(1, "the adjacent leaf hears it");
        Received(c).Should().Be(0, "a leaf does not relay, so the far node is unreachable");
    }

    [Fact]
    public void Ring_Dedup_Terminates()
    {
        // A ring of relays would flood forever without dedup; the ring must terminate and
        // each leaf still gets one copy.
        var f = new MeshFabric();
        var a = f.AddLeaf("A");
        f.AddRelay("R1"); f.AddRelay("R2"); f.AddRelay("R3"); f.AddRelay("R4");
        var b = f.AddLeaf("B");
        f.Connect("A", "R1");
        f.ConnectChain(["R1", "R2", "R3", "R4", "R1"]);   // R1-R2-R3-R4-R1 cycle
        f.Connect("R3", "B");

        a.SendDataAsync("z"u8.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

        Received(b).Should().Be(1, "delivered once despite the cycle");
    }

    [Fact]
    public void ScopedFlood_ContainedByOutOfScopeRelay()
    {
        // Model B: A(scope=uk) - R1(scope=uk) - R2(scope=other) - B. The scoped flood must
        // stop at R2 (wrong scope), so B never hears it.
        var f = new MeshFabric();
        var a = f.AddLeaf("A", scope: "uk");
        f.AddRelay("R1", scope: "uk");
        f.AddRelay("R2", scope: "other");
        var b = f.AddLeaf("B");
        f.ConnectChain(["A", "R1", "R2", "B"]);

        a.SendDataAsync("scoped"u8.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

        Received(b).Should().Be(0, "an out-of-scope relay drops the flood (containment)");
    }

    [Fact]
    public void ScopedFlood_CarriedByInScopeRelays()
    {
        // Control for the containment test: when every relay shares the scope, delivery works.
        var f = new MeshFabric();
        var a = f.AddLeaf("A", scope: "uk");
        f.AddRelay("R1", scope: "uk");
        f.AddRelay("R2", scope: "uk");
        var b = f.AddLeaf("B");
        f.ConnectChain(["A", "R1", "R2", "B"]);

        a.SendDataAsync("scoped"u8.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

        Received(b).Should().Be(1, "in-scope relays carry the flood to the destination");
    }

    [Fact]
    public void UnscopedFlood_CrossesRelaysRegardlessOfTheirScope()
    {
        // An unscoped flood (model A) is carried by any relay, even scoped ones.
        var f = new MeshFabric();
        var a = f.AddLeaf("A");                       // no scope
        f.AddRelay("R1", scope: "uk");
        f.AddRelay("R2", scope: "other");
        var b = f.AddLeaf("B");
        f.ConnectChain(["A", "R1", "R2", "B"]);

        a.SendDataAsync("open"u8.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

        Received(b).Should().Be(1, "unscoped floods propagate through every relay");
    }

    [Theory]
    [InlineData(64, 1)]    // 64 relays all forward (R64 receives path_len 63 < 64) -> B hears it
    [InlineData(65, 0)]    // the 65th relay would receive path_len 64 -> dropped -> B unreachable
    public void HopCap_DeliversAtExactly64Relays_DropsBeyond(int relayCount, int expected)
    {
        // The firmware cap is MAX_PATH_SIZE=64 forwarders (path_len < 64). Pin the exact
        // boundary - this is the long-chain edge the fabric exists to exercise.
        var f = new MeshFabric();
        f.AddLeaf("A");
        var chain = new List<string> { "A" };
        for (var i = 1; i <= relayCount; i++) { f.AddRelay($"R{i}"); chain.Add($"R{i}"); }
        f.AddLeaf("B");
        chain.Add("B");
        f.ConnectChain(chain);

        f.Link("A").SendDataAsync("edge"u8.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

        Received(f.Link("B")).Should().Be(expected);
    }

    [Theory]
    [InlineData("73 de M0LTE")]                                                  // one fragment
    [InlineData("Hello from the DAPPS mailbox, a longer store-and-forward message over the mesh that fragments. 73 de M0LTE GB7ABC-1 599 599 599")]  // multi-fragment
    public void Transport_MultiHop_DecodesOnceAtDestination(string text)
    {
        // The REAL bearer transport (encode + compress + fragment + nonce) carried across
        // 3 relay hops must reassemble/decode to the original message, exactly once.
        var f = new MeshFabric();
        var a = f.AddLeaf("A");
        f.AddRelay("R1"); f.AddRelay("R2"); f.AddRelay("R3");
        var b = f.AddLeaf("B");
        f.ConnectChain(["A", "R1", "R2", "R3", "B"]);

        var msg = new BackhaulMessage(
            Id: "abc0001", Destination: "GB7B-1", Salt: 7, Ttl: 3600,
            Payload: Encoding.UTF8.GetBytes(text), Originator: "GB7A-1", LinkSourceCallsign: "GB7A-1");

        // Encode + fragment + nonce at A, send each frame; B reassembles + decodes.
        var frames = new MeshCoreChannelTransport().ToFrames(msg, DappsCompression.Mode.ZstdDict);
        foreach (var frame in frames)
            a.SendDataAsync(frame, CancellationToken.None).GetAwaiter().GetResult();

        var rx = new MeshCoreChannelTransport();
        var got = new List<BackhaulMessage>();
        var batch = b.DrainAsync(CancellationToken.None).GetAwaiter().GetResult();
        batch.Should().NotBeNull("the destination leaf heard the frames");
        foreach (var d in batch!.Data)
        {
            var r = rx.Ingest(d.Payload, DateTime.UtcNow);
            if (r.Kind == MeshCoreChannelTransport.Kind.BackhaulComplete) got.Add(r.Message!);
        }

        got.Should().HaveCount(1, "the message reassembles/decodes exactly once");
        Encoding.UTF8.GetString(got[0].Payload).Should().Be(text);
        got[0].Id.Should().Be("abc0001");
    }
}
