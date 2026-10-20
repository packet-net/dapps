using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Drives the per-app bearer middleware against synthetic
/// <see cref="HttpContext"/>s. Covers the open-mode (auth disabled =
/// pass-through), closed-mode rejection paths, and the success case
/// where a controller-visible <c>HttpContext.Items</c> entry is left
/// stamped with the authenticated app.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class BearerAuthMiddlewareTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppTokenStore store = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-mw-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbAppToken>();
        }
        store = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AuthDisabled_PassesThroughWithoutToken()
    {
        var (mw, ctx, nextCalled) = Build(authRequired: false, "/AppApi/inbound/myapp");

        await mw.InvokeAsync(ctx);

        nextCalled().Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ctx.GetAuthenticatedApp().Should().BeNull();
    }

    [Fact]
    public async Task AuthEnabled_NonAppApiPath_PassesThrough()
    {
        // Auth only enforces /AppApi/* - admin surfaces stay open by
        // design (loopback recommendation), so /Config and /Neighbours
        // bypass the bearer check.
        var (mw, ctx, nextCalled) = Build(authRequired: true, "/Config");

        await mw.InvokeAsync(ctx);

        nextCalled().Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task AuthEnabled_NoAuthHeader_Returns401()
    {
        var (mw, ctx, nextCalled) = Build(authRequired: true, "/AppApi/inbound/myapp");

        await mw.InvokeAsync(ctx);

        nextCalled().Should().BeFalse("the next pipeline must not run for unauth'd requests");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ctx.Response.Headers["WWW-Authenticate"].ToString().Should().Contain("Bearer");
    }

    [Fact]
    public async Task AuthEnabled_InvalidToken_Returns401()
    {
        var (mw, ctx, nextCalled) = Build(authRequired: true, "/AppApi/inbound/myapp");
        ctx.Request.Headers["Authorization"] = "Bearer not-a-real-token";

        await mw.InvokeAsync(ctx);

        nextCalled().Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ctx.Response.Headers["WWW-Authenticate"].ToString().Should().Contain("invalid_token");
    }

    [Fact]
    public async Task AuthEnabled_ValidToken_PassesThroughWithAuthenticatedAppInItems()
    {
        var token = await store.CreateOrRotateAsync("myapp");
        var (mw, ctx, nextCalled) = Build(authRequired: true, "/AppApi/inbound/myapp");
        ctx.Request.Headers["Authorization"] = $"Bearer {token}";

        await mw.InvokeAsync(ctx);

        nextCalled().Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ctx.GetAuthenticatedApp().Should().Be("myapp");
    }

    [Fact]
    public void IsAuthorisedForApp_OpenMode_AlwaysTrue()
    {
        var ctx = new DefaultHttpContext();
        // No AuthenticatedAppKey set - open mode.
        ctx.IsAuthorisedForApp("anything").Should().BeTrue();
    }

    [Fact]
    public void IsAuthorisedForApp_AuthedAppMatches_True()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[BearerAuthMiddleware.AuthenticatedAppKey] = "myapp";
        ctx.IsAuthorisedForApp("myapp").Should().BeTrue();
    }

    [Fact]
    public void IsAuthorisedForApp_AuthedAppMismatches_False()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[BearerAuthMiddleware.AuthenticatedAppKey] = "myapp";
        ctx.IsAuthorisedForApp("yourapp").Should().BeFalse(
            "an authed app must not be allowed to scope into another app's resources");
    }

    [Fact]
    public async Task RepeatedBadTokens_FromOneAddress_AreLockedOutWith429_ButOtherAddressesAreNot()
    {
        var token = await store.CreateOrRotateAsync("myapp");
        var throttle = new AuthThrottle(TimeProvider.System);
        var bad = System.Net.IPAddress.Parse("203.0.113.7");
        var other = System.Net.IPAddress.Parse("203.0.113.8");

        for (var i = 0; i < AuthThrottle.FreeFailures + 1; i++)
        {
            var (mw, c, _) = Build(true, "/AppApi/inbound/myapp", throttle);
            c.Connection.RemoteIpAddress = bad;
            c.Request.Headers["Authorization"] = "Bearer wrong";
            await mw.InvokeAsync(c);
            c.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        }

        // Locked: even the right token is refused for that address, without a hash.
        var (mw2, locked, nextLocked) = Build(true, "/AppApi/inbound/myapp", throttle);
        locked.Connection.RemoteIpAddress = bad;
        locked.Request.Headers["Authorization"] = $"Bearer {token}";
        await mw2.InvokeAsync(locked);
        locked.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        locked.Response.Headers["Retry-After"].ToString().Should().NotBeEmpty();
        nextLocked().Should().BeFalse();

        // A different client is unaffected.
        var (mw3, fine, nextFine) = Build(true, "/AppApi/inbound/myapp", throttle);
        fine.Connection.RemoteIpAddress = other;
        fine.Request.Headers["Authorization"] = $"Bearer {token}";
        await mw3.InvokeAsync(fine);
        nextFine().Should().BeTrue();
    }

    [Fact]
    public async Task TwentySimultaneousValidRequests_FromOneRemoteAddress_AllSucceed()
    {
        var token = await store.CreateOrRotateAsync("myapp");
        var throttle = new AuthThrottle(TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            var (mw, c, next) = Build(true, "/AppApi/inbound/myapp", throttle);
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.50");
            c.Request.Headers["Authorization"] = $"Bearer {token}";
            await mw.InvokeAsync(c);
            return next() && c.Response.StatusCode == StatusCodes.Status200OK;
        }));
        results.Should().AllBeEquivalentTo(true);
    }

    [Fact]
    public async Task LoopbackIsNeverLockedOut()
    {
        var token = await store.CreateOrRotateAsync("myapp");
        var throttle = new AuthThrottle(TimeProvider.System);
        for (var i = 0; i < AuthThrottle.FreeFailures + 5; i++)
        {
            var (mw, c, _) = Build(true, "/AppApi/inbound/myapp", throttle);
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            c.Request.Headers["Authorization"] = "Bearer wrong";
            await mw.InvokeAsync(c);
        }
        var (mw2, ok, next) = Build(true, "/AppApi/inbound/myapp", throttle);
        ok.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        ok.Request.Headers["Authorization"] = $"Bearer {token}";
        await mw2.InvokeAsync(ok);
        next().Should().BeTrue("one bad client behind a local gateway must not lock out the rest");
    }

    private (BearerAuthMiddleware mw, DefaultHttpContext ctx, Func<bool> nextCalled) Build(
        bool authRequired, string path, AuthThrottle? throttle = null)
    {
        var called = false;
        RequestDelegate next = _ =>
        {
            called = true;
            return Task.CompletedTask;
        };

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            AuthRequired = authRequired,
        });
        var mw = new BearerAuthMiddleware(next, store, optionsMonitor, NullLogger<BearerAuthMiddleware>.Instance, throttle);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;

        return (mw, ctx, () => called);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
