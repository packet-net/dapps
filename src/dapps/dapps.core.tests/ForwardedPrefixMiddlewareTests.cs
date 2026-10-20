using System.Net;
using AwesomeAssertions;
using dapps.core.Services;
using Microsoft.AspNetCore.Http;

namespace dapps.core.tests;

/// <summary>#320: X-Forwarded-Prefix is honoured only from a proxy on this
/// machine, and only when it is a plain path.</summary>
public sealed class ForwardedPrefixMiddlewareTests
{
    [Theory]
    [InlineData("127.0.0.1", "/apps/dapps", "/apps/dapps")]
    [InlineData("127.0.0.1", "/apps/dapps/", "/apps/dapps")]
    [InlineData("::1", "/apps/dapps", "/apps/dapps")]
    [InlineData("::ffff:127.0.0.1", "/apps/dapps", "/apps/dapps")]
    public async Task FromLoopback_ASafePrefixIsUsed(string remote, string header, string expected)
        => (await PathBaseAfterAsync(remote, header)).Should().Be(expected);

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.45.0.5")]
    [InlineData("203.0.113.9")]
    [InlineData("2001:db8::1")]
    public async Task FromAnyoneElse_TheHeaderIsIgnored(string remote)
        => (await PathBaseAfterAsync(remote, "/apps/dapps")).Should().BeEmpty();

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("apps/dapps")]
    [InlineData("/a/../b")]
    [InlineData("/a//b")]
    [InlineData("/a\\b")]
    [InlineData("/a?x=1")]
    [InlineData("http://evil.example")]
    [InlineData("/%2e%2e/x")]
    public async Task EvenFromLoopback_ANonPathValueIsIgnored(string header)
        => (await PathBaseAfterAsync("127.0.0.1", header)).Should().BeEmpty();

    private static async Task<string> PathBaseAfterAsync(string remote, string header)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        ctx.Request.Headers[ForwardedPrefixMiddleware.HeaderName] = header;
        await new ForwardedPrefixMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);
        return ctx.Request.PathBase.Value ?? "";
    }
}
