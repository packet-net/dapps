namespace dapps.core.tests.Integration.Rig425;

/// <summary>
/// Brings a rig up and holds it for DAPPS_RIG_425_HOLD minutes, for poking
/// at by hand (on 127.0.0.1: 18500 up for all BPQ, 18600 XRouter, 18700 pdn, 18800 mixed). Only with DAPPS_RIG_425_HOLD set.
/// </summary>
public abstract class Rig425Hold(Rig425Mesh mesh)
{
    [Fact]
    public async Task Hold()
    {
        var minutes = Environment.GetEnvironmentVariable("DAPPS_RIG_425_HOLD");
        Assert.SkipUnless(Rig425Mesh.Wanted && minutes is { Length: > 0 }, "Set DAPPS_RIG_425 and DAPPS_RIG_425_HOLD to hold a rig up.");
        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine($"{mesh.Name} up after {mesh.SettleTime.TotalSeconds:F0} s");
        foreach (var n in mesh.Nodes) output.WriteLine($"{n}: AGW {n.AgwPort} RHP {n.RhpPort} HTTP {n.HttpPort} telnet {n.TelnetPort}");
        var log = new RigLog();
        var apps = new List<EchoApp>();
        foreach (var n in new[] { mesh.Calne, mesh.Peer1, mesh.Peer2 })
            apps.Add(await EchoApp.StartAsync(n, mesh.Host, log.Note, TestContext.Current.CancellationToken));
        File.WriteAllText("/tmp/rig425-up", mesh.Name + "\n" + await mesh.NodesTableAsync(mesh.Local));
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(double.Parse(minutes!));
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            File.WriteAllText("/tmp/rig425-echo.log", log.Text());
        }
        foreach (var app in apps) await app.DisposeAsync();
    }
}

[Collection("Rig425 all BPQ")]
[Trait("Category", "Integration")]
public sealed class Rig425HoldAllBpq(Rig425AllBpq mesh) : Rig425Hold(mesh);

[Collection("Rig425 all XRouter")]
[Trait("Category", "Integration")]
public sealed class Rig425HoldAllXrouter(Rig425AllXrouter mesh) : Rig425Hold(mesh);

[Collection("Rig425 all pdn")]
[Trait("Category", "Integration")]
public sealed class Rig425HoldAllPdn(Rig425AllPdn mesh) : Rig425Hold(mesh);

[Collection("Rig425 mixed")]
[Trait("Category", "Integration")]
public sealed class Rig425HoldMixed(Rig425Mixed mesh) : Rig425Hold(mesh);
