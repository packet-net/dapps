namespace dapps.core.tests.Integration;

/// <summary>
/// How a DAPPS daemon reaches its node: the bearer (AGW or RHPv2), where the
/// node listens for it, and the node port its neighbours are on, 0-indexed
/// as DAPPS counts them (DAPPS asks RHPv2 for that plus one).
/// </summary>
public sealed record NodeAttachment(string Host, string Bearer, int Port, int BearerPort)
{
    public static NodeAttachment Agw(string host, int agwPort, int bearerPort) => new(host, "agw", agwPort, bearerPort);

    public static NodeAttachment Rhp(string host, int rhpPort, int bearerPort) => new(host, "rhpv2", rhpPort, bearerPort);

    public bool IsRhp => Bearer == "rhpv2";
}

/// <summary>
/// Two packet nodes linked together, A and B, each with the callsign a DAPPS
/// daemon takes there and the way that daemon reaches its node. The tests
/// that run on any pair (BPQ over AXIP, pdn over AXUDP, one of each) take
/// this rather than a fixture.
/// </summary>
public interface IDappsNodePair
{
    /// <summary>What links the two nodes, for reports: e.g. "pdn, AXUDP".</summary>
    string ChannelName { get; }

    string ApplCallA { get; }
    string ApplCallB { get; }
    NodeAttachment NodeA { get; }
    NodeAttachment NodeB { get; }

    /// <summary>Start recording what each node hears from the other.</summary>
    Task<IAirMonitor> StartAirMonitorAsync(CancellationToken ct);
}

/// <summary>
/// A pair the crossed-call scenario and the ones on a simulated radio
/// channel run on: it can say how its radio ports are set, restart both
/// nodes as after a reboot, and (on a simulated channel) log when each
/// radio is on air.
/// </summary>
public interface IDappsScenarioBed : IDappsNodePair
{
    /// <summary>The nodes' radio-port settings, for reports.</summary>
    string RadioSettings { get; }

    /// <summary>Tells one pair's report files from another's: e.g. "afsk1200", "pdn-qpsk3600".</summary>
    string ReportTag { get; }

    /// <summary>When each radio transmits, or null when the link isn't a simulated radio channel.</summary>
    Task<ChannelLog?> StartChannelLogAsync(CancellationToken ct);

    /// <summary>Restart both nodes, as after a reboot, and don't wait for them.</summary>
    Task RestartNodesColdAsync();

    /// <summary>Wait until each node has been heard by the other.</summary>
    Task WaitUntilReadyAsync();

    /// <summary>Which of the pair's containers has stopped by itself, or
    /// null while all are running (or when they aren't watched).</summary>
    string? Died { get; }

    /// <summary>Throws, with the containers' logs and <paramref name="detail"/>,
    /// if one of them has stopped by itself.</summary>
    Task ThrowIfDiedAsync(string? detail = null);
}

/// <summary>
/// What went over the air between A and B, as each node heard it: frames
/// sent from A are the ones B heard. Each frame is one entry, a header in
/// BPQ's monitor style (<c>Fm N0AAA-3 To N0BBB-3 &lt;C C P&gt;</c> for a
/// connect, <c>&lt;D C</c> a disconnect, <c>&lt;I </c> an I-frame) with the
/// frame's text on the lines after it.
/// </summary>
public interface IAirMonitor : IAsyncDisposable
{
    /// <summary>Frames sent from <paramref name="from"/>'s side: heard by the other node.</summary>
    IReadOnlyList<string> SentBy(string from);

    int CountSentBy(string from, string contains);

    Task<bool> WaitForAsync(string from, string contains, TimeSpan timeout, CancellationToken ct);

    /// <summary>Everything heard, for a failure message.</summary>
    string Transcript();
}
