using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

[Collection(SqliteOverridePathCollection.Name)]
public sealed class AdminPasswordStoreTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-adminpwd-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using var c = DbInfo.GetConnection();
        c.CreateTable<DbSystemOption>();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FreshStore_IsNotConfigured()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        (await store.IsConfiguredAsync()).Should().BeFalse();
        (await store.VerifyAsync("anything")).Should().BeFalse(
            "verify against an unset store always returns false - never throws");
    }

    [Fact]
    public async Task SetAndVerify_RoundTripsTheCorrectPassword()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("hunter2hunter2");

        (await store.IsConfiguredAsync()).Should().BeTrue();
        (await store.VerifyAsync("hunter2hunter2")).Should().BeTrue();
        (await store.VerifyAsync("hunter3hunter3")).Should().BeFalse();
        (await store.VerifyAsync("")).Should().BeFalse();
        (await store.VerifyAsync(null)).Should().BeFalse();
    }

    [Fact]
    public async Task SetTwice_RotatesAndOldPasswordStopsWorking()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("first-password");
        await store.SetAsync("second-password");

        (await store.VerifyAsync("first-password")).Should().BeFalse();
        (await store.VerifyAsync("second-password")).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short12")]
    public async Task SetEmptyOrShort_IsRefused_AndKeepsTheOldPassword(string? attempt)
    {
        // There is no passwordless dashboard (#316): nothing can clear
        // the password, and a new one needs at least 8 characters.
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("original-password");

        var act = () => store.SetAsync(attempt);

        await act.Should().ThrowAsync<ArgumentException>();
        (await store.IsConfiguredAsync()).Should().BeTrue();
        (await store.VerifyAsync("original-password")).Should().BeTrue();
    }

    [Fact]
    public async Task SetShort_OnAFreshStore_IsRefused()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);

        var act = () => store.SetAsync("1234567");

        await act.Should().ThrowAsync<ArgumentException>();
        (await store.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AnExistingShortPassword_StillVerifies()
    {
        // Older versions had no server-side minimum. Upgrading must not
        // lock anyone out, so a short stored password keeps working.
        var salt = new byte[16];
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes("abc"), salt, 100_000,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbSystemOption { Option = "AdminPasswordHash", Value = Convert.ToHexString(hash) });
            c.Insert(new DbSystemOption { Option = "AdminPasswordSalt", Value = Convert.ToHexString(salt) });
        }
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);

        (await store.VerifyAsync("abc")).Should().BeTrue();
    }

    [Fact]
    public async Task Endpoint_ChangesThePassword_WhenTheCurrentOneIsRight()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("original-password");
        var ctrl = Controller(store);

        var result = await ctrl.SetAdminPassword(new AdminPasswordRequest("brand-new-password", "original-password"));

        result.Should().BeOfType<NoContentResult>();
        (await store.VerifyAsync("brand-new-password")).Should().BeTrue();
        (await store.VerifyAsync("original-password")).Should().BeFalse();
    }

    [Theory]
    [InlineData("brand-new-password", null)]
    [InlineData("brand-new-password", "")]
    [InlineData("brand-new-password", "wrong-password")]
    [InlineData("", "original-password")]
    [InlineData(null, "original-password")]
    [InlineData("short12", "original-password")]
    public async Task Endpoint_RefusesAWrongCurrentOrABadNewPassword(string? password, string? current)
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("original-password");
        var ctrl = Controller(store);

        var result = await ctrl.SetAdminPassword(new AdminPasswordRequest(password, current));

        result.Should().BeOfType<BadRequestObjectResult>();
        (await store.IsConfiguredAsync()).Should().BeTrue("nothing turns the login off");
        (await store.VerifyAsync("original-password")).Should().BeTrue();
    }

    [Fact]
    public async Task Endpoint_SignsTheCallerBackIn_AfterAChange()
    {
        // A change signs out every login issued before it (#385); the
        // browser that made the change gets a fresh one.
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("original-password");
        var auth = new RecordingAuthService();
        var ctrl = Controller(store, auth);

        await ctrl.SetAdminPassword(new AdminPasswordRequest("brand-new-password", "original-password"));

        auth.SignIns.Should().Be(1);
    }

    [Fact]
    public async Task Sessions_FromBeforeTheLastSet_AreNoLongerCurrent()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Nothing recorded yet (a password set by an older version):
        // existing logins carry on.
        (await store.IsSessionCurrentAsync(earlier)).Should().BeTrue();

        await store.SetAsync("original-password");

        (await store.IsSessionCurrentAsync(earlier)).Should().BeFalse();
        (await store.IsSessionCurrentAsync(null)).Should().BeFalse();
        (await store.IsSessionCurrentAsync(DateTimeOffset.UtcNow)).Should().BeTrue(
            "a login issued in the same second as the change, like the one made by the browser that changed it, stays");
    }

    [Fact]
    public async Task CookieValidation_RejectsAStaleLogin_AndKeepsACurrentOne()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("original-password");

        var stale = ValidationContext(store, DateTimeOffset.UtcNow.AddMinutes(-5), out var staleAuth);
        await AdminSession.ValidateAsync(stale);
        stale.Principal.Should().BeNull("a login from before the change is rejected");
        staleAuth.SignOuts.Should().Be(1, "and its cookie deleted");

        var current = ValidationContext(store, DateTimeOffset.UtcNow, out var currentAuth);
        await AdminSession.ValidateAsync(current);
        current.Principal.Should().NotBeNull();
        currentAuth.SignOuts.Should().Be(0);
    }

    private static CookieValidatePrincipalContext ValidationContext(
        AdminPasswordStore store, DateTimeOffset issued, out RecordingAuthService auth)
    {
        auth = new RecordingAuthService();
        var http = HttpContextWith(store, auth);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "sysop")], "Cookies"));
        var props = new AuthenticationProperties { IssuedUtc = issued };
        return new CookieValidatePrincipalContext(
            http,
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, props, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static DefaultHttpContext HttpContextWith(AdminPasswordStore store, IAuthenticationService auth) => new()
    {
        RequestServices = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(auth)
            .BuildServiceProvider(),
    };

    private static ConfigController Controller(AdminPasswordStore store, RecordingAuthService? auth = null) =>
        new(new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance), store)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = HttpContextWith(store, auth ?? new RecordingAuthService()),
            },
        };

    private sealed class RecordingAuthService : IAuthenticationService
    {
        public int SignIns { get; private set; }
        public int SignOuts { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignIns++;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignOuts++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task EachSet_UsesADifferentSalt()
    {
        var store = new AdminPasswordStore(NullLogger<AdminPasswordStore>.Instance);
        await store.SetAsync("same-password");
        var hash1 = ReadHash();
        await store.SetAsync("same-password");
        var hash2 = ReadHash();

        hash1.Should().NotBe(hash2,
            "fresh salt every set; same plaintext shouldn't produce the same hash");
    }

    private static string ReadHash()
    {
        using var c = DbInfo.GetConnection();
        return c.Find<DbSystemOption>("AdminPasswordHash")!.Value;
    }
}
