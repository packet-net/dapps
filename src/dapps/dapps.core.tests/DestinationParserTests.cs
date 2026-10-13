using dapps.core.Services;
using AwesomeAssertions;

namespace dapps.core.tests;

public class DestinationParserTests
{
    [Theory]
    [InlineData("appname@gb7aaa-4", "appname", "gb7aaa-4")]
    [InlineData("foo@N0CALL", "foo", "N0CALL")]
    [InlineData("multi-word-app@call-9", "multi-word-app", "call-9")]
    public void Parse_SplitsOnFirstAtSign(string destination, string expectedApp, string expectedCallsign)
    {
        var (app, callsign) = DestinationParser.Parse(destination);
        app.Should().Be(expectedApp);
        callsign.Should().Be(expectedCallsign);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign-here")]
    public void Parse_ReturnsEmptyOnMalformed(string destination)
    {
        var (app, callsign) = DestinationParser.Parse(destination);
        app.Should().BeEmpty();
        callsign.Should().BeEmpty();
    }

    [Theory]
    [InlineData("foo@N0CALL", "N0CALL", true)]
    [InlineData("foo@N0CALL-3", "N0CALL-3", true)]
    [InlineData("foo@N0CALL-3", "N0CALL", false)]                   // the SSID is part of the station (#239)
    [InlineData("foo@N0CALL", "N0CALL-1", false)]
    [InlineData("foo@N0CALL", "N0CALL-0", true)]                     // no SSID is SSID 0
    [InlineData("foo@n0call", "N0CALL", true)]                       // case-insensitive
    [InlineData("foo@G8BPQ", "N0CALL", false)]                       // different callsign
    [InlineData("", "N0CALL", false)]                                // malformed
    [InlineData("foo@", "N0CALL", false)]                            // empty callsign
    public void IsLocal_MatchesTheWholeCallsignCaseInsensitively(string destination, string local, bool expected)
    {
        DestinationParser.IsLocal(destination, local).Should().Be(expected);
    }
}
