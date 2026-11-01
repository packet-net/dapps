using dapps.core.tests.Integration.Rig425;

namespace dapps.core.tests.Integration.Rig430;

/// <summary>
/// Bring the #430 replica up, show each node's NET/ROM table, and run the
/// console commands in DAPPS_RIG_430_EXPLORE_CMDS ("NODE:command;..."),
/// then hold it up for DAPPS_RIG_430_EXPLORE_HOLD_MINUTES so it can be
/// looked at by hand. For working on the rig, not for results.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Rig", "430")]
public sealed class Rig430Explore
{
    [Fact]
    public async Task BringUp()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DAPPS_RIG_430_EXPLORE") is { Length: > 0 }, "Set DAPPS_RIG_430_EXPLORE to bring the #430 replica up.");
        var ct = TestContext.Current.CancellationToken;
        var log = new RigLog();
        await using var rig = new Rig430Network(log.Note);
        try
        {
            await rig.StartAsync(ct);
            foreach (var cmd in (Environment.GetEnvironmentVariable("DAPPS_RIG_430_EXPLORE_CMDS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                // NODE:cmd|cmd|cmd, one console session.
                var parts = cmd.Split(':', 2);
                var node = rig.Nodes.Single(n => n.Role == parts[0]);
                await using var console = await Rig430Console.OpenAsync(node.Kind, rig.Host, rig.TelnetPort(node), ct);
                foreach (var line in parts[1].Split('|'))
                {
                    log.Note($"{node.Real}> {line}: {RigLog.Clean(await console.CommandAsync(line, 8000, ct))}");
                }
            }
            var hold = double.TryParse(Environment.GetEnvironmentVariable("DAPPS_RIG_430_EXPLORE_HOLD_MINUTES"), out var m) ? m : 0;
            if (hold > 0)
            {
                log.Note($"holding for {hold} minutes; net-sim web on {rig.NetSimWebPort}; telnet ports " +
                    string.Join(", ", rig.Nodes.Select(n => $"{n.Real} {rig.TelnetPort(n)}")));
                await Task.Delay(TimeSpan.FromMinutes(hold), ct);
            }
        }
        catch (Exception e)
        {
            log.Note($"FAILED: {e}");
            log.Note(await rig.LogsAsync());
            throw;
        }
        finally
        {
            TestContext.Current.TestOutputHelper?.WriteLine(log.Text());
        }
    }
}
