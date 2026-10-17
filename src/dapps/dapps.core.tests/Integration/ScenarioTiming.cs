namespace dapps.core.tests.Integration;

/// <summary>
/// The simulated-radio scenarios run in real time on whatever host runs
/// them, so how long one takes depends on how busy that host is as well
/// as on DAPPS (#352). Each scenario has a target, what the exchange takes
/// with room to spare on a quiet host, which goes in its report as a
/// measurement, and a hard ceiling at twice the target, past which the
/// test fails. A report that misses its target says so with
/// <see cref="OverTargetMarker"/>, which CI looks for and raises as a
/// warning.
/// </summary>
internal static class ScenarioTiming
{
    public const string OverTargetMarker = "Over the time target";

    public static TimeSpan Ceiling(TimeSpan target) => target * 2;

    /// <summary>The report line for one measurement against its target.</summary>
    public static string Line(string what, TimeSpan took, TimeSpan target) =>
        took <= target
            ? $"{what}: {took.TotalSeconds:F1} s, within the {target.TotalSeconds:F0} s target (hard ceiling {Ceiling(target).TotalSeconds:F0} s)."
            : $"**{OverTargetMarker}:** {what} took {took.TotalSeconds:F1} s against a {target.TotalSeconds:F0} s target (hard ceiling {Ceiling(target).TotalSeconds:F0} s). A busy host can do this; a run that keeps doing it is worth a look.";
}

/// <summary>
/// The seed for what a scenario picks at random (which node submits first,
/// and how far apart), written in its report so a failure can be replayed:
/// set <c>DAPPS_SCENARIO_SEED</c> to it. That replays the scenario's own
/// choices, not the simulated channel's timing or the host's load.
/// </summary>
internal static class ScenarioSeed
{
    public const string Variable = "DAPPS_SCENARIO_SEED";

    public static int Pick() =>
        int.TryParse(Environment.GetEnvironmentVariable(Variable), out var seed) ? seed : Random.Shared.Next();

    public static string Line(int seed) =>
        $"Seed: {seed} (set {Variable}={seed} to replay the same choices).";
}
