using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// Spins up a single ghcr.io/packethacking/xrouter container with its AGW
/// emulator and its RHPv2 port exposed to the host. AGW frame-format tests
/// run against XRouter's AGW emulator alongside BPQ's, proving DAPPS isn't
/// BPQ-specific; RHPv2 is how DAPPS is meant to run on XRouter
/// (docs/connect/xrouter.md), and two daemons on this one node exercise it.
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
///   shipped config example has <c>AGWPORT=8000 8000</c>, which sets
///   the XRouter stack's and the host stack's port separately; one arg
///   sets both.</item>
/// <item>ACCESS.SYS decides whether an AGW client must log in with a
///   password, and 505o enforces it where 505c didn't: a client that
///   doesn't log in gets no answer to any frame, not even the version
///   ('R'). The image's stock ACCESS.SYS asks a password of every address
///   outside 44/8 and 192.168.0/24, which takes in Docker's bridge and
///   loopback, so the fixture brings its own (see <see cref="AccessSys"/>).
///   RHPv2 isn't affected.</item>
/// <item>The image's entrypoint runs xrouter in headless mode and
///   tails /data/LOG/*.TXT to stdout, so docker logs / Testcontainers
///   wait-for-log strategies pick up boot lines once the daemon is
///   up.</item>
/// </list>
/// </summary>
public sealed class XrouterIntegrationFixture : IAsyncLifetime
{
    /// <summary>
    /// Pinned by digest, like the other test images, so the tests only move
    /// to a new XRouter on purpose: XRouter 505c, the image built on
    /// 3 May 2026. CI pulls whatever digest this line names. To try
    /// another build first, set DAPPS_XROUTER_IMAGE to its image reference.
    ///
    /// Not 505o (built 11 October 2026): its reply to an RHPv2 bind isn't
    /// valid JSON (packethacking/xrouter-container#2), so DAPPS never gets
    /// to listen and <see cref="XrouterRhpEndToEndTests"/> fails. The AGW
    /// test passes on 505o. The pin moves once XRouter publishes a build
    /// with a valid bind reply.
    /// </summary>
    public const string PinnedImage = "ghcr.io/packethacking/xrouter@sha256:6a885470e19d5c8ee3033d224a30a8675d5b1957428a832aa89746f37ec0b611";

    public static string Image =>
        Environment.GetEnvironmentVariable("DAPPS_XROUTER_IMAGE") is { Length: > 0 } other ? other : PinnedImage;

    private const int InsideAgwPort = 8000;
    private const int InsideRhpPort = 9000;

    public string Host => "127.0.0.1";
    public int AgwPort { get; private set; }
    public int RhpPort { get; private set; }

    public string LocalCallsign => "G9DUM-1";

    /// <summary>Bearer port equivalent on XRouter. The
    /// AGW emulator dispatches on the per-port byte the same as BPQ;
    /// 0 corresponds to the first INTERFACE/PORT pair (the loopback
    /// in the test config). Over RHPv2 DAPPS asks for that port as "1".</summary>
    public int BearerPortIndex => 0;

    /// <summary>
    /// Test harness only: any valid callsign may connect, with no password,
    /// from anywhere (flags 1). The interop rig in xrouter-container uses the
    /// same. Without it XRouter 505o ignores the AGW tests' frames.
    /// </summary>
    private const string AccessSys = "0.0.0.0/0 1\n";

    private IContainer? _container;

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(RenderConfig()), "/data/XROUTER.CFG")
            .WithResourceMapping(Encoding.UTF8.GetBytes(AccessSys), "/data/ACCESS.SYS")
            .WithPortBinding(InsideAgwPort, assignRandomHostPort: true)
            .WithPortBinding(InsideRhpPort, assignRandomHostPort: true)
            // XRouter takes a couple of seconds to bind its ports; the
            // entrypoint's tail-LOG/*.TXT pattern means we get a
            // "started" line only when xrouter is fully up. Port-
            // available is the simpler ready signal.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(InsideAgwPort)
                .UntilInternalTcpPortIsAvailable(InsideRhpPort))
            .Build();

        await _container.StartAsync();
        AgwPort = _container.GetMappedPublicPort(InsideAgwPort);
        RhpPort = _container.GetMappedPublicPort(InsideRhpPort);

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
        // Loopback-only setup: what goes out of port 1 comes straight back
        // in, so a daemon that opens a link to another daemon's callsign on
        // port 1 reaches that daemon's listener on this same node.
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
        $"RHPPORT={InsideRhpPort}\n" +
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
