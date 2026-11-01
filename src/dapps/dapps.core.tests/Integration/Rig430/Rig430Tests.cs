using System.Text.Json;
using dapps.core.tests.Integration.Rig425;

namespace dapps.core.tests.Integration.Rig430;

/// <summary>
/// The MB7NPW replica for packet-net/dapps#430 (<see cref="Rig430Network"/>):
/// real DAPPS daemons on MB7NPW and its three peers behind CALNE, once per
/// arm (<see cref="Rig430Arm"/>) and workload (<see cref="Rig430Workload"/>),
/// repeated. Each run writes scenario-reports/rig430-&lt;arm&gt;-&lt;workload&gt;-&lt;rep&gt;-&lt;time&gt;.md
/// and a line to scenario-reports/rig430-results.jsonl; rig430-summarise.py
/// beside this file makes the tables.
///
/// Opt-in, never in normal CI: skipped unless DAPPS_RIG_430 is set. Real
/// time, about half an hour per repetition of both workloads, so run it on
/// our own machines. Settings: DAPPS_RIG_430_ARMS (A,LL; L, LL, S and SL
/// in Rig430Arm.All, S and SL need DAPPS_RIG_430_DIALAS_DLL), DAPPS_RIG_430_REPS (5),
/// DAPPS_RIG_430_WORKLOADS (im,burst), DAPPS_RIG_430_IM_MINUTES (10),
/// DAPPS_RIG_430_IM_GAP_SECONDS (60), DAPPS_RIG_430_USER_GAP_SECONDS (45),
/// DAPPS_RIG_430_BPQ_IMAGE (LinBPQ for MB7NPW, GB7LOX and EI7KX),
/// DAPPS_RIG_430_PATH_LOSS_DB (130), DAPPS_RIG_430_NPR_DELAY_MS (250).
/// DAPPS_RIG_425_LIVE names a file for progress lines.
///
///     cd src/dapps/dapps.core.tests
///     DAPPS_RIG_430=1 DAPPS_RIG_430_ARMS=A dotnet run -c Release -- --filter-class dapps.core.tests.Integration.Rig430.Rig430Tests
/// </summary>
[Trait("Category", "Integration")]
[Trait("Rig", "430")]
public sealed class Rig430Tests
{
    public static bool Wanted => Environment.GetEnvironmentVariable("DAPPS_RIG_430") is { Length: > 0 };

    private static int Reps => int.TryParse(Environment.GetEnvironmentVariable("DAPPS_RIG_430_REPS"), out var n) && n > 0 ? n : 5;

    [Fact]
    public async Task RunSelectedArms()
    {
        Assert.SkipUnless(Wanted, "Set DAPPS_RIG_430 to run the #430 MB7NPW replica (real time, hours).");
        var ct = TestContext.Current.CancellationToken;
        var log = new RigLog();
        var dir = Path.Combine(AppContext.BaseDirectory, "scenario-reports");
        Directory.CreateDirectory(dir);
        var results = Path.Combine(dir, "rig430-results.jsonl");
        var failures = new List<string>();

        foreach (var arm in Rig430Arm.Selected())
        {
            if (arm.DaemonDll is not null && arm.DaemonDll() is not { Length: > 0 })
            {
                failures.Add($"arm {arm.Id}: no daemon build given (DAPPS_RIG_430_DIALAS_DLL)");
                continue;
            }
            log.Note($"arm {arm.Id}: {arm.Title}; starting the replica");
            await using var rig = new Rig430Network(log.Note);
            await rig.StartAsync(ct);
            for (var rep = 1; rep <= Reps; rep++)
            {
                foreach (var workload in Rig430Workload.Selected())
                {
                    var seed = ScenarioSeed.Pick();
                    var run = new Rig430Run(rig, arm, workload, rep, seed, log.Note);
                    var r = await run.RunAsync(ct);
                    var name = $"rig430-{arm.Id}-{workload.Id}-{rep}-{DateTime.UtcNow:HHmmss}.md";
                    await File.WriteAllTextAsync(Path.Combine(dir, name), run.Report, ct);
                    await File.AppendAllTextAsync(results, JsonSerializer.Serialize(r) + "\n", ct);
                    var air = string.Join(", ", r.Links.Where(kv => kv.Key != "npr").Select(kv => $"{kv.Key} {kv.Value.Airtime:F0} s"));
                    log.Note($"arm {arm.Id} {workload.Id} rep {rep}: median {r.LatencyMedian:F1} s, p90 {r.LatencyP90:F1} s, replies median {r.ReplyLatencyMedian:F1} s, " +
                        $"last {r.LastDelivery:F0} s, {r.Delivered}/{r.Messages} + {r.RepliesDelivered}/{r.Replies} delivered, {r.Duplicates} duplicates, airtime {air}, " +
                        $"{r.Dials} dials{(r.Failure is null ? "" : ", FAILED: " + r.Failure.Split('\n')[0])}; {name}");
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
