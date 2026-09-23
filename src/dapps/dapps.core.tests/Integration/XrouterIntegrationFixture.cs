using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// Spins up a single ghcr.io/packethacking/xrouter container with AGW
/// exposed to the host. Mirrors <see cref="LinbpqIntegrationFixture"/>'s
/// shape so AGW frame-format and behavioural tests can run against
/// XRouter's AGW emulator alongside BPQ's, proving DAPPS isn't
/// BPQ-specific.
///
/// XRouter (Paula Dowie, G8PZT) is the most common BPQ alternative
/// in the British packet community. Its AGW emulator reports a
/// different version stamp (2000.20) to BPQ (2003.999) but otherwise
/// implements the AGW protocol byte-for-byte the same way.
///
/// Config quirks worth knowing:
/// <list type="bullet">
/// <item>NODECALL must look like a real callsign with letters and
///   digits - "N0CALL-1" is rejected as "Invalid argument" because
///   the 0-as-second-letter trips XRouter's callsign validator. Use
///   "G9DUM-1" (the example callsign from the shipped config).</item>
/// <item>LOCATOR is required and must be a valid 6-char Maidenhead
///   grid (e.g. "IO91PM"); "NONE" / "IO99ZE" / etc. are rejected.</item>
/// <item><c>AGWPORT=8000</c> takes a single arg (the TCP port). The
///   shipped config example has <c>AGWPORT=8000 8000</c> which the
///   .CFG.MAN doesn't actually document; one arg works fine.</item>
/// <item>The image's entrypoint runs xrouter in headless mode and
///   tails /data/LOG/*.TXT to stdout, so docker logs / Testcontainers
///   wait-for-log strategies pick up boot lines once the daemon is
///   up.</item>
/// </list>
/// </summary>
public sealed class XrouterIntegrationFixture : IAsyncLifetime
{
    private const string Image = "ghcr.io/packethacking/xrouter:latest";
    private const int InsideAgwPort = 8000;

    public string Host => "127.0.0.1";
    public int AgwPort { get; private set; }

    public string LocalCallsign => "G9DUM-1";

    /// <summary>Bearer port equivalent on XRouter. The
    /// AGW emulator dispatches on the per-port byte the same as BPQ;
    /// 0 corresponds to the first INTERFACE/PORT pair (the loopback
    /// in the test config).</summary>
    public int BearerPortIndex => 0;

    private IContainer? _container;

    public async ValueTask InitializeAsync()
    {
        var configBytes = Encoding.UTF8.GetBytes(RenderConfig());

        _container = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(configBytes, "/data/XROUTER.CFG")
            .WithPortBinding(InsideAgwPort, assignRandomHostPort: true)
            // XRouter takes a couple of seconds to bind AGW; the
            // entrypoint's tail-LOG/*.TXT pattern means we get a
            // "started" line only when xrouter is fully up. Port-
            // available is the simpler ready signal.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwPort))
            .Build();

        await _container.StartAsync();
        AgwPort = _container.GetMappedPublicPort(InsideAgwPort);

        // Match the BPQ fixture's tiny grace; XRouter's AGW emulator
        // also rejects the very first connect occasionally as the
        // event loop catches up.
        await Task.Delay(2000);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private string RenderConfig() =>
        // Loopback-only setup. No real radio interfaces means no
        // routable AX.25, but the AGW emulator itself works fine for
        // frame-format tests.
        //
        // DNS is required because xrouter is statically linked and
        // can't use the libc resolver. 8.8.8.8 is the README's
        // suggested default.
        "DNS=8.8.8.8\n" +
        $"NODECALL={LocalCallsign}\n" +
        "NODEALIAS=DAPPST\n" +
        "LOCATOR=IO91PM\n" +
        "CONSOLECALL=G9DUM\n" +
        "CHATCALL=G9DUM-8\n" +
        "CHATALIAS=DTCHAT\n" +
        $"AGWPORT={InsideAgwPort}\n" +
        "INTERFACE=1\n" +
        "\tTYPE=LOOPBACK\n" +
        "\tPROTOCOL=KISS\n" +
        "\tMTU=256\n" +
        "ENDINTERFACE\n" +
        "PORT=1\n" +
        "\tID=\"Loopback port\"\n" +
        "\tINTERFACENUM=1\n" +
        "ENDPORT\n";
}

[CollectionDefinition("XRouter integration", DisableParallelization = true)]
public class XrouterIntegrationCollection : ICollectionFixture<XrouterIntegrationFixture> { }
