using System.Diagnostics;
using AwesomeAssertions;
using dapps.core.tests.Integration;

namespace dapps.core.tests;

/// <summary>
/// #330: a daemon a test starts must not pick up DAPPS_* or PDN_* values
/// from the test process. Release run 38769754963 failed twice because a
/// daemon inherited PDN_APP_CALLSIGN=M9YYY-7 from a unit test and bound
/// that callsign instead of its own.
/// </summary>
public sealed class ChildEnvironmentTests
{
    [Fact]
    public void Apply_DropsInheritedDappsAndPdnKeys_KeepsTheRest_AndSetsTheTestsOwn()
    {
        var start = new ProcessStartInfo("dotnet");
        start.Environment["PDN_APP_CALLSIGN"] = "M9YYY-7";
        start.Environment["pdn_node_callsign"] = "M9YYY";
        start.Environment["DAPPS_CALLSIGN"] = "G0OLD";
        start.Environment["DAPPS_ENV_MANAGED"] = "true";
        start.Environment["PATH"] = "/usr/bin";

        ChildEnvironment.Apply(start.Environment, new Dictionary<string, string>
        {
            ["DAPPS_CALLSIGN"] = "N0AAA-9",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
        });

        start.Environment.Keys.Where(ChildEnvironment.IsDappsOrPdnKey)
            .Should().BeEquivalentTo(["DAPPS_CALLSIGN"]);
        start.Environment["DAPPS_CALLSIGN"].Should().Be("N0AAA-9");
        start.Environment["ASPNETCORE_URLS"].Should().Be("http://127.0.0.1:0");
        start.Environment["PATH"].Should().Be("/usr/bin");
    }
}
