using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="AppTokensController"/> directly. Covers the
/// admin-surface contracts the README's getting-started guide leans on:
/// POST returns the plaintext exactly once, GET surfaces names without
/// leaking secrets, DELETE 404s when nothing to revoke, validation
/// rejects empty and malformed app names and lowercases valid ones (see
/// <see cref="AppTokenStoreNameValidationTests"/> for the exhaustive
/// coverage of what <see cref="AppTokenStore.TryNormalizeAppName"/>
/// accepts and rejects).
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class AppTokensControllerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private AppTokenStore store = null!;
    private AppTokensController controller = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-tokens-ctrl-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbAppToken>();
        }
        store = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
        controller = new AppTokensController(store);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreateOrRotate_ReturnsPlaintextTokenAndPersists()
    {
        var result = await controller.CreateOrRotate(new CreateAppTokenRequest("myapp"));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<CreateAppTokenResponse>().Subject;

        body.App.Should().Be("myapp");
        body.Token.Should().NotBeNullOrEmpty();

        // Token verifies.
        (await store.VerifyAsync(body.Token)).Should().Be("myapp");
    }

    [Fact]
    public async Task CreateOrRotate_EmptyApp_ReturnsBadRequest()
    {
        var result = await controller.CreateOrRotate(new CreateAppTokenRequest(""));
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("chat/room")]
    [InlineData("chat+")]
    [InlineData("chat#")]
    [InlineData("my app")]
    public async Task CreateOrRotate_AppNameWithBannedChars_ReturnsBadRequest(string app)
    {
        var result = await controller.CreateOrRotate(new CreateAppTokenRequest(app));
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateOrRotate_LowercasesTheAppName()
    {
        var result = await controller.CreateOrRotate(new CreateAppTokenRequest("Chat"));
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<CreateAppTokenResponse>().Subject;
        body.App.Should().Be("chat");
    }

    [Fact]
    public async Task CreateOrRotate_TrimsAppName()
    {
        var result = await controller.CreateOrRotate(new CreateAppTokenRequest("  myapp  "));
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<CreateAppTokenResponse>().Subject;
        body.App.Should().Be("myapp");
    }

    [Fact]
    public async Task List_ReturnsAppNamesWithoutSecrets()
    {
        await store.CreateOrRotateAsync("alpha");
        await store.CreateOrRotateAsync("beta");

        var entries = (await controller.List()).ToList();

        entries.Should().HaveCount(2);
        entries.Select(e => e.AppName).Should().BeEquivalentTo(new[] { "alpha", "beta" });
    }

    [Fact]
    public async Task Revoke_Existing_ReturnsNoContent()
    {
        await store.CreateOrRotateAsync("alpha");
        var result = await controller.Revoke("alpha");
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Revoke_Absent_ReturnsNotFound()
    {
        var result = await controller.Revoke("nope");
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task RotateThenList_ShowsExactlyOneEntryWithNewCreatedAt()
    {
        var first = await store.CreateOrRotateAsync("alpha");
        var entries1 = (await controller.List()).ToList();
        var firstCreatedAt = entries1.Single().CreatedAt;

        // Sleep enough that ticks differ.
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var second = await store.CreateOrRotateAsync("alpha");

        var entries2 = (await controller.List()).ToList();
        entries2.Should().ContainSingle("rotation must not duplicate");
        entries2.Single().CreatedAt.Should().BeAfter(firstCreatedAt);
        first.Should().NotBe(second);
    }
}
