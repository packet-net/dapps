namespace dapps.core.tests.Integration.Rig425;

/// <summary>
/// What the #425 rig observed, per topology, test and row, as
/// <see cref="Rig425Experiments"/> words it. A run that sees anything else
/// fails and prints the difference.
/// </summary>
internal static class Rig425Expected
{
    public static readonly Dictionary<(string Topology, string Test, string Row), string> Table = new()
    {
    };
}
