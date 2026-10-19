using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

/// <summary>The /mqtt Origin rule (#314). The end-to-end upgrade is
/// covered in Integration/SetupAndMqttOriginSecurityTests.</summary>
public sealed class MqttWebSocketOriginMiddlewareTests
{
    [Theory]
    [InlineData("", "node:5000", "", true)]                              // non-browser client, no Origin
    [InlineData("http://node:5000", "node:5000", "", true)]              // same origin
    [InlineData("http://NODE:5000", "node:5000", "", true)]              // host is case-insensitive
    [InlineData("http://node", "node:80", "", true)]                     // default port either way
    [InlineData("https://gw.example", "127.0.0.1:5000", "gw.example", true)] // behind a proxy
    [InlineData("http://evil.example", "node:5000", "", false)]
    [InlineData("http://node:5001", "node:5000", "", false)]             // another port is another origin
    [InlineData("https://node:5000", "node:5000", "", true)]             // scheme follows the Origin, port matches
    [InlineData("null", "node:5000", "", false)]                          // sandboxed frame or file://
    [InlineData("http://evil.example", "node:5000", "elsewhere.example", false)]
    public void OpenMode(string origin, string host, string forwardedHost, bool allowed) =>
        MqttWebSocketOriginMiddleware.IsAllowed(origin, host, forwardedHost, authRequired: false).Should().Be(allowed);

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    public void TokensRequired_AnyOriginReachesTheConnectCheck(string origin) =>
        MqttWebSocketOriginMiddleware.IsAllowed(origin, "node:5000", "", authRequired: true).Should().BeTrue();
}
