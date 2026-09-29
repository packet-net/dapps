using dapps.client.Transport.Agw;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.client.Transport.Rhp;

/// <summary>
/// RHPv2 (Remote Host Protocol v2) outbound transport. Drop-in
/// equivalent of <see cref="AgwOutboundTransport"/> for hosts that
/// expose RHPv2 instead of (or alongside) AGW - XRouter is the
/// motivating case.
///
/// Each <see cref="ConnectAsync"/> opens a fresh TCP connection to
/// the host's RHPv2 port (default 9000), opens an AX.25 stream
/// session in active mode (RHP socket(family=ax25,mode=stream) +
/// connect), and returns a Stream wired to the resulting handle.
/// Session bytes flow through <see cref="MultiplexedAgwSessionStream"/>
/// (the class is bearer-agnostic despite the AGW prefix in the name -
/// it's just a Pipe-backed Stream with two callbacks).
///
/// XRouter-specific quirk that motivated this transport: AGW scopes
/// callsign authorisation per-TCP-connection, so DAPPS's
/// per-outbound-fresh-connection pattern collides with
/// AgwInboundService's standing X-frame registration. RHPv2 has no
/// equivalent claim - each TCP connection is independent and
/// authorisation is per-handle-bind. So opening a fresh RhpClient
/// per outbound forward works on XR without any of the AGW
/// double-registration drama.
///
/// There's no monitor to spot a crossed call by, as the AGW transport
/// does. pdn (0.57.0 on) says so itself instead: its open reply carries
/// <c>"crossed": true</c> when the peer's call to us arrived while it was
/// dialling, or the link was already up. Then no prompt is coming, and
/// <see cref="IDappsConnection.CrossedCall"/> says so. XRouter never
/// sends the key, and without it the session waits for a prompt as before.
/// </summary>
public sealed class Rhpv2OutboundTransport : IDappsOutboundTransport
{
    private readonly string host;
    private readonly int port;
    private readonly string? authUser;
    private readonly string? authPass;
    private readonly ILogger logger;
    private readonly IDappsTxGate txGate;

    public Rhpv2OutboundTransport(
        string host, int port,
        ILogger<Rhpv2OutboundTransport> logger,
        string? authUser = null, string? authPass = null,
        IDappsTxGate? txGate = null)
    {
        this.host = host;
        this.port = port;
        this.logger = logger;
        this.authUser = authUser;
        this.authPass = authPass;
        this.txGate = txGate ?? AlwaysOpenTxGate.Instance;
    }

    public async Task<IDappsConnection> ConnectAsync(
        string localCallsign, string remoteCallsign, int bearerPort, CancellationToken stoppingToken)
    {
        logger.LogInformation("RHP: connecting to {host}:{port}", host, port);
        var rhp = await RhpClient.ConnectAsync(host, port, stoppingToken);
        try
        {
            if (!string.IsNullOrEmpty(authUser))
            {
                await rhp.AuthenticateAsync(authUser, authPass ?? "", stoppingToken);
            }

            // RHP port-byte addressing: the bearerPort here is DAPPS's
            // bearer-neutral "AGW port byte". RHPv2 takes the port as a
            // string label that XRouter resolves to an INTERFACE/PORT
            // pair. XR's convention is that port="1" means PORT=1 in
            // XROUTER.CFG, etc. - 1-indexed, where DAPPS's port byte is
            // 0-indexed. Add one and convert.
            var portName = (bearerPort + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // RF-emitting: an active OpenAsync triggers an AX.25 SABM on
            // the remote port. Block here when the gate is closed.
            if (!txGate.TxAllowed)
            {
                throw new TxStoppedException(
                    $"RHP active open {localCallsign}->{remoteCallsign} on port {portName}: {txGate.BlockReason ?? "(no reason)"}");
            }

            // Stream view of the handle. Reuses MultiplexedAgwSessionStream
            // (functionally generic - just a Pipe + 2 callbacks).
            // RF-emitting: SendOnHandleAsync emits AX.25 I-frames. Gate
            // each call so a session opened before TX-stop becomes
            // silent the moment the operator hits the kill-switch.
            // CloseAsync stays ungated to avoid leaking handles.
            var gate = txGate;
            var handle = 0;
            var stream = new MultiplexedAgwSessionStream(
                writeOutgoing: async (data, c) =>
                {
                    if (!gate.TxAllowed)
                    {
                        throw new TxStoppedException(
                            $"RHP send on handle {handle}: {gate.BlockReason ?? "(no reason)"}");
                    }
                    await rhp.SendOnHandleAsync(handle, data, c);
                },
                sendRemoteDisconnect: async c =>
                {
                    try { await rhp.CloseAsync(handle, c); }
                    catch { /* server may have already closed */ }
                });

            // Listen from before the open. A node that answers the open
            // only once the far end's UA is in (pdn does) can push the
            // far end's first data, or its hang-up, straight behind the
            // reply, and RhpClient raises those events before the code
            // after OpenAsync runs: added afterwards, the handlers missed
            // the peer's prompt. So what arrives before the handle is
            // known is held, then handed over in order.
            var early = new List<(int Handle, byte[]? Data)>();   // Data null: the node closed it
            int? opened = null;
            EventHandler<RhpReceivedEventArgs> recvHandler = (_, e) =>
            {
                var bytes = RhpDataEncoding.FromWireString(e.Message.Data);
                lock (early)
                {
                    if (opened is null) early.Add((e.Message.Handle, bytes));
                    // Fire-and-forget: PushIncoming awaits the pipe write,
                    // which is bounded only by the consumer's read pace.
                    else if (e.Message.Handle == opened) _ = stream.PushIncoming(bytes, CancellationToken.None);
                }
            };
            EventHandler<RhpClosedEventArgs> closeHandler = (_, e) =>
            {
                lock (early)
                {
                    if (opened is null) early.Add((e.Handle, null));
                    else if (e.Handle == opened) stream.SignalRemoteDisconnect();
                }
            };
            rhp.Received += recvHandler;
            rhp.Closed += closeHandler;

            logger.LogInformation("RHP: open active {local}->{remote} on port {p}", localCallsign, remoteCallsign, portName);
            var reply = await rhp.OpenWithReplyAsync(
                family: ProtocolFamily.Ax25,
                mode: SocketMode.Stream,
                port: portName,
                local: localCallsign,
                remote: remoteCallsign,
                flags: OpenFlags.Active,
                ct: stoppingToken);
            handle = reply.Handle;

            lock (early)
            {
                opened = handle;
                foreach (var (_, data) in early.Where(x => x.Handle == handle))
                {
                    if (data is null) stream.SignalRemoteDisconnect();
                    else _ = stream.PushIncoming(data, CancellationToken.None);
                }
                early.Clear();
            }

            // pdn's word that the peer was calling us too (see the class
            // doc). Absent, as from XRouter, means it can't tell.
            var crossed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (reply.Crossed == true)
            {
                logger.LogInformation("RHP: the node says {remote} was calling us as we called it: the calls crossed", remoteCallsign);
                crossed.TrySetResult();
            }

            return new Rhpv2Connection(rhp, stream, recvHandler, closeHandler, handle, logger, crossed.Task);
        }
        catch
        {
            await rhp.DisposeAsync();
            throw;
        }
    }
}

internal sealed class Rhpv2Connection : IDappsConnection
{
    private readonly RhpClient rhp;
    private readonly MultiplexedAgwSessionStream stream;
    private readonly EventHandler<RhpReceivedEventArgs> recvHandler;
    private readonly EventHandler<RhpClosedEventArgs> closeHandler;
    private readonly int handle;
    private readonly ILogger logger;
    private bool disposed;

    public Rhpv2Connection(
        RhpClient rhp,
        MultiplexedAgwSessionStream stream,
        EventHandler<RhpReceivedEventArgs> recvHandler,
        EventHandler<RhpClosedEventArgs> closeHandler,
        int handle,
        ILogger logger,
        Task crossedCall)
    {
        this.rhp = rhp;
        this.stream = stream;
        this.recvHandler = recvHandler;
        this.closeHandler = closeHandler;
        this.handle = handle;
        this.logger = logger;
        CrossedCall = crossedCall;
    }

    public Stream Stream => stream;

    public Task CrossedCall { get; }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { rhp.Received -= recvHandler; } catch { }
        try { rhp.Closed -= closeHandler; } catch { }
        try { await rhp.CloseAsync(handle, CancellationToken.None); }
        catch (Exception ex) { logger.LogDebug(ex, "RHP: close({h}) failed (may have already closed)", handle); }
        await stream.DisposeAsync();
        await rhp.DisposeAsync();
    }
}
