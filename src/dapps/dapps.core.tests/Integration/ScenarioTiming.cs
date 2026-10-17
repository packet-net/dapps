namespace dapps.core.tests.Integration;

/// <summary>
/// The simulated-radio scenarios run in real time on whatever host runs
/// them, so how long one takes depends on how busy that host is as well
/// as on DAPPS (#352). Each scenario has a target, what the exchange takes
/// with room to spare on a quiet host, and a hard ceiling at twice that.
///
/// Missing the target fails the test, tagged <see cref="OverTargetTag"/>:
/// CI (scripts/ci-test.py) reruns such a test once, so one slow run on a
/// busy runner passes on the rerun and is named in the job summary, and a
/// second miss fails the job. Missing the ceiling, or messages that never
/// arrive, fails tagged <see cref="OverCeilingTag"/>, which CI never
/// reruns. The report records the measurement either way, with
/// <see cref="OverTargetMarker"/> on a miss.
/// </summary>
internal static class ScenarioTiming
{
    public const string OverTargetMarker = "Over the time target";

    /// <summary>In a failure message: over the target, worth one rerun in CI.</summary>
    public const string OverTargetTag = "[over-target]";

    /// <summary>In a failure message: over the hard ceiling, never rerun in CI.</summary>
    public const string OverCeilingTag = "[over-ceiling]";

    public static TimeSpan Ceiling(TimeSpan target) => target * 2;

    /// <summary>The report line for one measurement against its target.</summary>
    public static string Line(string what, TimeSpan took, TimeSpan target) =>
        took <= target
            ? $"{what}: {took.TotalSeconds:F1} s, within the {target.TotalSeconds:F0} s target (hard ceiling {Ceiling(target).TotalSeconds:F0} s)."
            : $"**{OverTargetMarker}:** {what} took {took.TotalSeconds:F1} s against a {target.TotalSeconds:F0} s target (hard ceiling {Ceiling(target).TotalSeconds:F0} s).";

    /// <summary>Fails the test if <paramref name="took"/> is over the ceiling, then if it is over the target, each with its tag.
    /// Call it after every check on what arrived, so a wrong result is never reported as a slow one.</summary>
    public static void AssertWithin(string what, TimeSpan took, TimeSpan target, string detail)
    {
        if (took > Ceiling(target))
        {
            Assert.Fail($"{OverCeilingTag} {what} took {took.TotalSeconds:F1} s, over the {Ceiling(target).TotalSeconds:F0} s hard ceiling (target {target.TotalSeconds:F0} s)\n{detail}");
        }
        if (took > target)
        {
            Assert.Fail($"{OverTargetTag} {what} took {took.TotalSeconds:F1} s, over the {target.TotalSeconds:F0} s target\n{detail}");
        }
    }
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
