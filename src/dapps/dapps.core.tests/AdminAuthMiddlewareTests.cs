using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Pinning the AdminAuthMiddleware's pass-through allowlist so a future
/// refactor can't silently re-gate <c>/Health</c> or <c>/Operational</c>.
/// Both are reached by clients that have no admin cookie (systemd
/// watchdog, external uptime monitors, MQTT-side scrapers); a 302 to
/// /Login defeats their purpose.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AdminAuthMiddlewareTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AdminPasswordStore store = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-admin-mw-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
        }
        // Set an admin password so the middleware's "no password →
        // /Setup" branch doesn't shadow the real test concern.
        store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        store.SetAsync("password-for-test").GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("/Health")]
    [InlineData("/Operational")]
    [InlineData("/Operational/recent")]
    [InlineData("/mcp")]
    [InlineData("/mcp/sse")]
    [InlineData("/AppApi/inbound/chat")]
    [InlineData("/Setup")]
    [InlineData("/Login")]
    [InlineData("/Logout")]
    [InlineData("/css/site.css")]
    [InlineData("/favicon.ico")]
    public async Task PassThroughPaths_NoAuth_ReachNextDelegate(string path)
    {
        var (mw, ctx, nextCalled) = Build(path);

        await mw.InvokeAsync(ctx, store, BuildOptionsStore());

        nextCalled().Should().BeTrue();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Config")]
    [InlineData("/Neighbours")]
    [InlineData("/Probes")]
    public async Task GatedPaths_NoCookie_DoNotReachNextDelegate(string path)
    {
        var (mw, ctx, nextCalled) = Build(path);

        // Without an admin cookie, the middleware triggers the cookie
        // auth scheme's challenge - which on a real pipeline turns into
        // a 302 to /Login. The synthetic HttpContext doesn't have an
        // authentication service registered, so ChallengeAsync throws -
        // catch broadly because the relevant assertion is just that
        // next() didn't run for these gated paths.
        try { await mw.InvokeAsync(ctx, store, BuildOptionsStore()); } catch { /* expected */ }

        nextCalled().Should().BeFalse();
    }

    [Fact]
    public async Task RetryNowAction_NoCookie_DoesNotReachNextDelegate()
    {
        // Reads under /Operational are open for scrapers and the
        // dashboard poll; the retry-now action under the same prefix is
        // not, so an unauthenticated POST must be challenged like any
        // other gated path.
        var (mw, ctx, nextCalled) = Build("/Operational/retry-now", "POST");

        try { await mw.InvokeAsync(ctx, store, BuildOptionsStore()); } catch { /* expected */ }

        nextCalled().Should().BeFalse();
    }

    private SystemOptionsStore BuildOptionsStore()
    {
        // Pre-seed a real callsign so the middleware's "callsign is the
        // placeholder → /Setup" branch doesn't kick in for these tests.
        // The setup-wizard redirect is its own test concern.
        using (var c = DbInfo.GetConnection())
        {
            c.InsertOrReplace(new DbSystemOption { Option = "Callsign", Value = "M0LTE-9" });
        }
        return new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
    }

    private static (AdminAuthMiddleware mw, DefaultHttpContext ctx, Func<bool> nextCalled) Build(string path, string method = "GET")
    {
        var called = false;
        RequestDelegate next = _ =>
        {
            called = true;
            return Task.CompletedTask;
        };

        var mw = new AdminAuthMiddleware(next);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;

        return (mw, ctx, () => called);
    }
}
