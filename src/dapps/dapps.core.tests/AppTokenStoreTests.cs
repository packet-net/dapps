using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests the per-app credential store (Plan A4). Tokens are PBKDF2-
/// hashed at rest and never returned after the initial creation;
/// verification is constant-time-equality on the derived bytes.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AppTokenStoreTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppTokenStore store = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-tokens-test-{Guid.NewGuid():N}.db");
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
    public async Task CreateOrRotate_ReturnsPlaintextThatVerifiesToTheRightApp()
    {
        var plaintext = await store.CreateOrRotateAsync("myapp");

        plaintext.Should().NotBeNullOrEmpty();
        (await store.VerifyAsync(plaintext)).Should().Be("myapp");
    }

    [Fact]
    public async Task CreateOrRotate_RotateInvalidatesPriorToken()
    {
        var first = await store.CreateOrRotateAsync("myapp");
        var second = await store.CreateOrRotateAsync("myapp");

        first.Should().NotBe(second, "rotation MUST regenerate fresh entropy");
        (await store.VerifyAsync(first)).Should().BeNull("the prior token must stop verifying after rotation");
        (await store.VerifyAsync(second)).Should().Be("myapp");
    }

    [Fact]
    public async Task NewToken_HasALookupIdAndVerifiesRepeatedly()
    {
        var token = await store.CreateOrRotateAsync("myapp");
        var row = (await DbInfo.GetAsyncConnection().Table<DbAppToken>().ToListAsync()).Single();
        row.TokenId.Should().NotBeNullOrEmpty();
        row.TokenId.Should().NotContain(token, "the id is a hash, not the token");

        for (var i = 0; i < 3; i++) (await store.VerifyAsync(token)).Should().Be("myapp");
    }

    [Fact]
    public async Task RevokeInAnotherStoreInstance_StopsAVerifiedTokenAtOnce()
    {
        var token = await store.CreateOrRotateAsync("myapp");
        (await store.VerifyAsync(token)).Should().Be("myapp"); // now remembered
        var other = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        await other.RevokeAsync("myapp");
        (await store.VerifyAsync(token)).Should().BeNull("the remembered success must not outlive the row");
    }

    [Fact]
    public async Task RowsFromBeforeTheIdColumn_StillVerifyAndGetAnIdWithoutReissue()
    {
        var token = await store.CreateOrRotateAsync("old");
        var other = await store.CreateOrRotateAsync("other");
        await DbInfo.GetAsyncConnection().ExecuteAsync("update apptokens set TokenId = null");

        (await store.VerifyAsync(token)).Should().Be("old");
        var rows = await DbInfo.GetAsyncConnection().Table<DbAppToken>().ToListAsync();
        rows.Single(r => r.AppName == "old").TokenId.Should().NotBeNullOrEmpty("filled in on first use");
        rows.Single(r => r.AppName == "other").TokenId.Should().BeNullOrEmpty();

        (await store.VerifyAsync(other)).Should().Be("other");
        (await store.VerifyAsync("not-a-token")).Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_UnknownToken_ReturnsNull()
    {
        await store.CreateOrRotateAsync("a");
        (await store.VerifyAsync("not-a-real-token-string")).Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_EmptyToken_ReturnsNull()
    {
        await store.CreateOrRotateAsync("a");
        (await store.VerifyAsync("")).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_RemovesTheRow()
    {
        var token = await store.CreateOrRotateAsync("myapp");
        var deleted = await store.RevokeAsync("myapp");

        deleted.Should().BeTrue();
        (await store.VerifyAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_AbsentApp_ReturnsFalse()
    {
        var deleted = await store.RevokeAsync("nope");
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_ReturnsAppNamesNotSecrets()
    {
        await store.CreateOrRotateAsync("alpha");
        await store.CreateOrRotateAsync("beta");

        var entries = await store.ListAsync();

        entries.Should().HaveCount(2);
        entries.Select(e => e.AppName).Should().BeEquivalentTo(new[] { "alpha", "beta" });
    }

    [Fact]
    public async Task StoredHash_NotPlaintext()
    {
        var plaintext = await store.CreateOrRotateAsync("myapp");

        using var c = DbInfo.GetConnection();
        var row = c.Find<DbAppToken>("myapp")!;
        row.TokenHash.Should().NotContain(plaintext, "the plaintext must not appear in the hash column");
        row.Salt.Should().HaveLength(32, "salt is 16 random bytes hex-encoded");
        row.TokenHash.Should().HaveLength(64, "hash is 32 derived bytes hex-encoded");
    }

    // --- Case-insensitive app-name matching (#316 follow-up review) ---
    // New mints go through TryNormalizeAppName and so are always stored
    // lowercase, but a row written before that existed could still carry
    // mixed case - CreateOrRotateAsync and RevokeAsync must still find it.

    [Fact]
    public async Task CreateOrRotate_PreExistingMixedCaseRow_RotatesInPlaceRatherThanDuplicating()
    {
        // Simulate a row from before case-normalisation existed.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbAppToken
            {
                AppName = "Chat",
                TokenHash = Convert.ToHexString(new byte[32]),
                Salt = Convert.ToHexString(new byte[16]),
                CreatedAt = DateTime.UtcNow.AddDays(-1),
            });
        }

        var newToken = await store.CreateOrRotateAsync("chat");

        var entries = await store.ListAsync();
        entries.Should().ContainSingle("rotating 'chat' must replace the pre-existing 'Chat' row, not add a second one");
        (await store.VerifyAsync(newToken)).Should().NotBeNull("the freshly minted token for 'chat' must verify");
    }

    [Fact]
    public async Task RevokeAsync_MatchesPreExistingMixedCaseRow()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbAppToken
            {
                AppName = "Chat",
                TokenHash = Convert.ToHexString(new byte[32]),
                Salt = Convert.ToHexString(new byte[16]),
                CreatedAt = DateTime.UtcNow,
            });
        }

        var deleted = await store.RevokeAsync("chat");

        deleted.Should().BeTrue("revoking 'chat' must find the pre-existing 'Chat' row case-insensitively");
        (await store.ListAsync()).Should().BeEmpty();
    }

    /// <summary>Databases from before names were normalised can hold both
    /// "Chat" and "chat". Rotating must replace both, not fail on the
    /// unique key after deleting only one.</summary>
    [Fact]
    public async Task CreateOrRotate_ReplacesEveryCaseVariantOfALegacyName()
    {
        var oldUpper = await store.CreateOrRotateAsync("Chat");
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbAppToken
            {
                AppName = "chat",
                TokenHash = Convert.ToHexString(new byte[32]),
                Salt = Convert.ToHexString(new byte[16]),
                CreatedAt = DateTime.UtcNow,
            });
        }

        var fresh = await store.CreateOrRotateAsync("chat");

        (await store.ListAsync()).Should().ContainSingle();
        (await store.VerifyAsync(fresh)).Should().Be("chat");
        (await store.VerifyAsync(oldUpper)).Should().BeNull("the legacy 'Chat' token must stop working");
    }

    [Fact]
    public async Task RevokeAsync_RemovesEveryCaseVariantOfALegacyName()
    {
        var upper = await store.CreateOrRotateAsync("Chat");
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update apptokens set AppName = 'Chat'");
        }
        var lower = await store.CreateOrRotateAsync("other");
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update apptokens set AppName = 'chat' where AppName = 'other'");
        }

        (await store.RevokeAsync("chat")).Should().BeTrue();

        (await store.VerifyAsync(upper)).Should().BeNull();
        (await store.VerifyAsync(lower)).Should().BeNull();
        (await store.ListAsync()).Should().BeEmpty();
    }

}

/// <summary>Tests for the static app-name validator used by both the
/// REST endpoint and the dashboard page, so both reject the same
/// inputs with the same reasoning.</summary>
public sealed class AppTokenStoreNameValidationTests
{
    [Theory]
    [InlineData("chat", "chat")]
    [InlineData("  chat  ", "chat")]
    [InlineData("Chat", "chat")]
    [InlineData("CHAT", "chat")]
    [InlineData("my-app_1", "my-app_1")]
    public void TryNormalizeAppName_ValidInput_LowercasesAndTrims(string raw, string expected)
    {
        var ok = AppTokenStore.TryNormalizeAppName(raw, out var normalized, out var error);

        ok.Should().BeTrue();
        normalized.Should().Be(expected);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("chat/room")]
    [InlineData("chat+")]
    [InlineData("chat#")]
    [InlineData("my app")]
    [InlineData("chat\t1")]
    [InlineData("chat\n1")]
    [InlineData("chat\u0001")]
    [InlineData("\u212Aelvin")]
    [InlineData("caf\u00e9")]
    public void TryNormalizeAppName_RejectsBlankSlashPlusHashWhitespaceAndControlChars(string? raw)
    {
        var ok = AppTokenStore.TryNormalizeAppName(raw, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryNormalizeAppName_AllowsQuotesAndOtherPunctuation_NotBannedForMqttOrRest()
    {
        // These aren't MQTT topic or REST path separators, so they're
        // allowed - the dashboard must render them safely regardless
        // (see AppTokensPageTests' hostile-name UI coverage), not reject
        // them here.
        var ok = AppTokenStore.TryNormalizeAppName("o'brien", out var normalized, out var error);

        ok.Should().BeTrue();
        normalized.Should().Be("o'brien");
        error.Should().BeNull();
    }
}
