using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
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

    private static ConfigController Controller(AdminPasswordStore store) =>
        new(new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance), store);

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
