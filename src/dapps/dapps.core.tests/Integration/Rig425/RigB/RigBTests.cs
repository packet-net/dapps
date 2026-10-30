using System.Text.Json;

namespace dapps.core.tests.Integration.Rig425.RigB;

/// <summary>
/// Rig B for packet-net/dapps#425: real DAPPS daemons delivering mail from
/// MB7NPW to three peers behind CALNE, the MB7NPW to CALNE hop on a
/// simulated AFSK 1200 channel (<see cref="RigBNetwork"/>), once per arm
/// (<see cref="RigBArm"/>) and workload (<see cref="RigBWorkload"/>),
/// repeated. Each run writes scenario-reports/rigb-&lt;arm&gt;-&lt;workload&gt;-&lt;rep&gt;.md
/// and a line to scenario-reports/rigb-results.jsonl; rigb-summarise.py
/// beside this file turns those lines into the tables on the issue.
///
/// Opt-in, never in normal CI: skipped unless DAPPS_RIG_425_B is set. It
/// runs in real time (an hour or more per arm), so run it on our own
/// machines. Settings:
/// DAPPS_RIG_425_B_ARMS (default 1,2,3,4,5,6), DAPPS_RIG_425_B_REPS (3),
/// DAPPS_RIG_425_B_WORKLOADS (burst,spread), DAPPS_RIG_425_B_PER_PEER (6
/// messages a peer), DAPPS_RIG_425_B_SPREAD_MINUTES (8), and the
/// DAPPS_RIG_425_B_PROTO_* settings of arm P. DAPPS_RIG_425_B_BPQ_IMAGE runs
/// another LinBPQ build on all five nodes, and DAPPS_RIG_425_B_BPQ_V20=1 sets
/// OnlyVer2point0 on them (the node-link SREJ study, linbpq-repro/). Arms G,
/// G3 and Gs run MB7NPW on the build at DAPPS_RIG_425_B_GIVEWAY_DLL;
/// DAPPS_RIG_425_B_MAIL_PEERS gives mail to only the first N peers; the both
/// workload has the peers send MB7NPW mail at the same moment. With DAPPS_RIG_425_LIVE set to
/// a file, progress lines go there as they happen.
///
///     cd src/dapps/dapps.core.tests
///     DAPPS_RIG_425_B=1 DAPPS_RIG_425_B_ARMS=1 dotnet run -c Release -- --filter-class dapps.core.tests.Integration.Rig425.RigB.RigBTests
/// </summary>
[Trait("Category", "Integration")]
[Trait("Rig", "425B")]
public sealed class RigBTests
{
    public static bool Wanted => Environment.GetEnvironmentVariable("DAPPS_RIG_425_B") is { Length: > 0 };

    private static int Reps => int.TryParse(Environment.GetEnvironmentVariable("DAPPS_RIG_425_B_REPS"), out var n) && n > 0 ? n : 3;

    [Fact]
    public async Task RunSelectedArms()
    {
        Assert.SkipUnless(Wanted, "Set DAPPS_RIG_425_B to run Rig B for #425 (real time, an hour or more per arm).");
        var ct = TestContext.Current.CancellationToken;
        var log = new RigLog();
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        var results = Path.Combine(dir, "rigb-results.jsonl");
        var failures = new List<string>();

        foreach (var arm in RigBArm.Selected())
        {
            if (arm.DaemonDll is not null && arm.DaemonDll() is not { Length: > 0 })
            {
                failures.Add($"arm {arm.Id}: no daemon build given (DAPPS_RIG_425_B_PROTO_DLL or DAPPS_RIG_425_B_GIVEWAY_DLL)");
                continue;
            }
            log.Note($"arm {arm.Id}: {arm.Title}; starting the {arm.Profile.Id} rig");
            await using var rig = new RigBNetwork(arm.Profile, log.Note);
            await rig.StartAsync(ct);
            for (var rep = 1; rep <= Reps; rep++)
            {
                foreach (var workload in RigBWorkload.Selected())
                {
                    var seed = ScenarioSeed.Pick();
                    var run = new RigBRun(rig, arm, workload, rep, seed, log.Note);
                    var r = await run.RunAsync(ct);
                    var name = $"rigb-{arm.Id}-{workload.Id}-{rep}-{DateTime.UtcNow:HHmmss}.md";
                    await File.WriteAllTextAsync(Path.Combine(dir, name), run.Report, ct);
                    await File.AppendAllTextAsync(results, JsonSerializer.Serialize(r) + "\n", ct);
                    log.Note($"arm {arm.Id} {workload.Id} rep {rep}: last delivery {r.LastDelivery:F0} s, " +
                        $"{r.Delivered}/{r.Messages} delivered, {r.Duplicates} duplicates, airtime {r.AirtimeToLast:F0} s ({r.BusyPercent:F0}% busy), " +
                        $"{r.Collisions} collisions, {r.FramesLostOnAir} frames lost{(r.Failure is null ? "" : ", FAILED: " + r.Failure.Split('\n')[0])}; {name}");
                    if (r.Failure is not null) failures.Add($"arm {arm.Id} {workload.Id} rep {rep}: {r.Failure}");
                    if (rig.Died is not null) break;
                }
                if (rig.Died is { } died)
                {
                    failures.Add($"arm {arm.Id}: {died}\n{await rig.LogsAsync()}");
                    break;
                }
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine(log.Text());
        Assert.True(failures.Count == 0, string.Join("\n\n", failures) + "\n\n" + log.Text());
    }
}
