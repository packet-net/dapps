namespace dapps.core.tests.Integration;

/// <summary>
/// The environment for a DAPPS daemon a test starts as a subprocess. A
/// child inherits this test process's whole environment, and a stray
/// <c>DAPPS_*</c> or <c>PDN_*</c> variable changes how the daemon starts
/// (a <c>PDN_APP_CALLSIGN</c> makes it bind that callsign verbatim, #330).
/// So every inherited key with either prefix goes before the test's own
/// are applied.
/// </summary>
internal static class ChildEnvironment
{
    public static bool IsDappsOrPdnKey(string key) =>
        key.StartsWith("DAPPS_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("PDN_", StringComparison.OrdinalIgnoreCase);

    public static void Apply(IDictionary<string, string?> environment, IReadOnlyDictionary<string, string> own)
    {
        foreach (var key in environment.Keys.Where(IsDappsOrPdnKey).ToList()) environment.Remove(key);
        foreach (var (key, value) in own) environment[key] = value;
    }
}
