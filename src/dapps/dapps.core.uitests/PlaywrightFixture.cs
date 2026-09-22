using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Owns a single shared Chromium instance for the UI test session.
/// First call drives a one-off browser install via
/// <see cref="Microsoft.Playwright.Program.Main"/> - idempotent, no
/// out-of-band setup, and works the same locally and in CI.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // Idempotent - silently no-ops if Chromium is already on disk.
        // Exit code != 0 means the install failed; throw early so the
        // first test gets a useful error rather than a vague navigate
        // timeout 30 s later.
        var installExitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (installExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Playwright Chromium install failed (exit {installExitCode}). " +
                "Run `pwsh src/dapps/dapps.core.uitests/bin/Debug/net10.0/playwright.ps1 install chromium` manually to diagnose.");
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        Playwright?.Dispose();
    }
}

/// <summary>
/// xunit collection definition so the WebApp + Playwright fixtures
/// are shared across every UI test (one app boot, one browser
/// process for the whole suite).
/// </summary>
[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<WebAppFixture>, ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "UI";
}
