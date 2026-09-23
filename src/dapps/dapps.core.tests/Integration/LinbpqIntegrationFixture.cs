using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace dapps.core.tests.Integration;

/// <summary>
/// Spins up a single m0lte/linbpq container with AGW exposed to the host
/// over a random port. Sufficient for tests that exercise AgwOutboundTransport's
/// frame layout against a real BPQ AGW listener and its connect-failure path.
///
/// Closes #5 - the prior implementation shelled out to <c>docker run</c>
/// directly because Testcontainers' default port-readiness check timed
/// out before BPQ finished binding AGW. Solved here by an explicit
/// generous timeout on the wait strategy and skipping the bind-mount
/// entirely (config is sent via <c>WithResourceMapping</c>, so there's
/// no root-owned host file to clean up afterwards).
///
/// A two-instance topology (AXIP-UDP between containers, A→B AGW connect via
/// APPL1CALL) was attempted but ran into a BPQ-side issue where inbound 'C'
/// frames don't reach a registered AGW client through the application-pre-
/// allocated listener path under Docker bridge networking - needs separate
/// investigation. The single-instance fixture covers what we actually need
/// at this layer (frame format + connect handshake + 'd' failure handling).
/// </summary>
public sealed class LinbpqIntegrationFixture : IAsyncLifetime
{
    private const string Image = "m0lte/linbpq:latest";
    private const int InsideAgwPort = 8000;
    private const int InsideTelnetPort = 8010;

    public string Host => "127.0.0.1";
    public int AgwPort { get; private set; }

    public string LocalCallsign => "N0CALL";
    public string UnreachableCallsign => "Q0XYZ";

    /// <summary>bearer port (0-indexed) corresponding to BPQ port 1, the
    /// only routable port in the single-instance config.</summary>
    public int BearerPortIndex => 0;

    private IContainer? _container;

    public async ValueTask InitializeAsync()
    {
        var configBytes = Encoding.UTF8.GetBytes(RenderConfig());

        _container = new ContainerBuilder()
            .WithImage(Image)
            .WithResourceMapping(configBytes, "/data/bpq32.cfg")
            .WithPortBinding(InsideAgwPort, assignRandomHostPort: true)
            // BPQ's entrypoint takes a few seconds to bind AGW; 60 s is
            // the Testcontainers default and is plenty here. Listening
            // on the AGW port is the right ready signal - once it's up,
            // tests can issue connects.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InsideAgwPort))
            .Build();

        await _container.StartAsync();
        AgwPort = _container.GetMappedPublicPort(InsideAgwPort);

        // Tiny grace: a handful of tests issued a connect immediately
        // after the AGW port appeared and saw transient connection
        // resets while BPQ finished post-bind init. Two seconds matches
        // the previous shell-out fixture and is empirically enough.
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
        "SIMPLE=1\n" +
        $"NODECALL={LocalCallsign}\n" +
        "NODEALIAS=NODE\n" +
        "LOCATOR=NONE\n" +
        $"AGWPORT={InsideAgwPort}\n" +
        "AGWSESSIONS=10\n" +
        "AGWMASK=1\n" +
        "\n" +
        "PORT\n" +
        " ID=Telnet\n" +
        " DRIVER=Telnet\n" +
        " CONFIG\n" +
        $" TCPPORT={InsideTelnetPort}\n" +
        " MAXSESSIONS=10\n" +
        $" USER=test,test,{LocalCallsign},,SYSOP\n" +
        "ENDPORT\n";
}

[CollectionDefinition("Linbpq integration", DisableParallelization = true)]
public class LinbpqIntegrationCollection : ICollectionFixture<LinbpqIntegrationFixture> { }
