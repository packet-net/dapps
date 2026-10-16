using System.Net.Sockets;
using System.Text;
using dapps.client.Tx;
using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// AGW-protocol implementation of <see cref="IDappsOutboundTransport"/>.
/// Each call to <see cref="ConnectAsync"/> opens a fresh TCP socket to the
/// node, asks for an AX.25 connection, and returns a Stream once the remote
/// confirms.
///
/// The socket also turns on the node's monitor, to catch a crossed call:
/// the peer's SABM to us arriving while ours is on its way to it, both
/// ends having dialled at once. Neither end is answering then, so no
/// prompt is coming; <see cref="IDappsConnection.CrossedCall"/> says so,
/// and the session doesn't wait for one.
/// </summary>
public sealed class AgwOutboundTransport : IDappsOutboundTransport
{
    private readonly string host;
    private readonly int port;
    private readonly ILogger logger;
    private readonly IDappsTxGate txGate;
    private readonly TimeProvider clock;

    /// <summary>Longest wait for the node to confirm or refuse a connect
    /// request, from the moment it goes out. Monitored frames arriving
    /// meanwhile do not extend it.</summary>
    public static readonly TimeSpan ConnectConfirmDeadline = TimeSpan.FromMinutes(3);

    public AgwOutboundTransport(string host, int port, ILoggerFactory loggerFactory, IDappsTxGate? txGate = null, TimeProvider? timeProvider = null)
    {
        this.host = host;
        this.port = port;
        this.logger = loggerFactory.CreateLogger<AgwOutboundTransport>();
        this.txGate = txGate ?? AlwaysOpenTxGate.Instance;
        this.clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<IDappsConnection> ConnectAsync(
        string localCallsign,
        string remoteCallsign,
        int bearerPort,
        CancellationToken stoppingToken)
    {
        if (bearerPort < 0 || bearerPort > AgwFrameTransport.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(bearerPort), $"AGW port must be 0 to {AgwFrameTransport.MaxPort}");
        }
        var portByte = (byte)bearerPort;

        var tcp = new TcpClient();
        try
        {
            logger.LogInformation("AGW: connecting to {host}:{port}", host, port);
            await tcp.ConnectAsync(host, port, stoppingToken);
            var ns = tcp.GetStream();
            var framing = new AgwFrameTransport(ns, txGate, logger);

            // Register our local callsign first. Without this BPQ has no
            // valid source for the SABM and emits frames with a blank src
            // field - the remote ignores them and we time out with
            // RETRYOUT. (linbpq's own AGW two-instance test does this for
            // the same reason.)
            //
            // Reply payload semantics differ slightly between AGW hosts:
            //   BPQ:     0x01 on success.
            //   XRouter: 0x01 if the callsign was free and is now ours,
            //            0x00 if it's already in use - including by our
            //            own AgwInboundService on a separate AGW
            //            connection (XR scopes registration per-TCP-
            //            connection, BPQ tolerates duplicate registers
            //            from anyone).
            // For DAPPS specifically, "already registered" is the normal
            // state when AgwInboundService is up. Treat both replies as
            // OK and proceed to the C-frame; if the host actually
            // rejects the connect on the source field, we'll see a 'd'
            // failure on the connect-confirm read instead.
            logger.LogDebug("AGW: registering {local}", localCallsign);
            await framing.WriteFrameAsync(
                new AgwFrame(0, 'X', 0, localCallsign, "", []),
                stoppingToken);
            var registerReply = await framing.ReadRegisterReplyAsync(localCallsign, clock, stoppingToken);
            if (registerReply.Payload.Length != 1)
            {
                throw new IOException(
                    $"AGW register {localCallsign} unexpected reply (kind '{registerReply.Kind}', payload [{string.Join(',', registerReply.Payload)}])");
            }
            if (registerReply.Payload[0] != 0x01 && registerReply.Payload[0] != 0x00)
            {
                throw new IOException(
                    $"AGW register {localCallsign} failed (kind 'X', payload [{registerReply.Payload[0]}])");
            }
            if (registerReply.Payload[0] == 0x00)
            {
                logger.LogDebug("AGW: {local} already registered on this host (e.g. by AgwInboundService); proceeding", localCallsign);
            }

            // Monitor what the node hears, to spot the peer calling us at
            // the same moment (see the class doc).
            var crossed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Watch(AgwFrame monitored)
            {
                if (crossed.Task.IsCompleted) return;
                if (IsPeerCallingUs(monitored, portByte, localCallsign, remoteCallsign))
                {
                    logger.LogInformation("AGW: {0} is calling us as we call it: the calls crossed", remoteCallsign);
                    crossed.TrySetResult();
                }
            }
            await framing.WriteFrameAsync(new AgwFrame(0, 'm', 0, "", "", []), stoppingToken);

            logger.LogInformation("AGW: requesting {local}->{remote} on port {p}", localCallsign, remoteCallsign, portByte);
            await framing.WriteFrameAsync(
                new AgwFrame(portByte, 'C', 0xF0, localCallsign, remoteCallsign, []),
                stoppingToken);

            // Wait for the connect to confirm. BPQ replies with 'C' echoing
            // the request on success, or 'd' on failure. Other frames may
            // arrive ahead of the answer (e.g. 'G' if we'd queried, port
            // descriptors, monitor traffic); skip them.
            //
            // One overall deadline for the answer (Plan A3, #278): if BPQ
            // never answers, the wait ends with a TimeoutException rather
            // than wedging the forwarder run forever. A per-read timeout
            // would not do: the monitor is on, so on a busy channel every
            // monitored frame would restart it.
            using var deadline = new CancellationTokenSource(ConnectConfirmDeadline, clock);
            using var confirmWait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
            while (true)
            {
                AgwFrame frame;
                try
                {
                    frame = await framing.ReadFrameAsync(confirmWait.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"AGW: the node did not answer the connect to {remoteCallsign} within {ConnectConfirmDeadline.TotalMinutes:F0} minutes");
                }

                // BPQ's 'C' confirmation comes back with callfrom/callto
                // swapped relative to the request - the remote (the one we
                // dialed) is now the FROM and we're the TO. Accept either
                // orientation defensively, since the spec is ambiguous and
                // some clients don't swap.
                if (frame.Kind == 'C')
                {
                    var matchesAsConfirm =
                        string.Equals(frame.CallFrom, remoteCallsign, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(frame.CallTo, localCallsign, StringComparison.OrdinalIgnoreCase);
                    var matchesAsEcho =
                        string.Equals(frame.CallFrom, localCallsign, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(frame.CallTo, remoteCallsign, StringComparison.OrdinalIgnoreCase);

                    if (matchesAsConfirm || matchesAsEcho)
                    {
                        logger.LogInformation("AGW: connect confirmed ({0})", Printable(frame.Payload));
                        var sessionStream = new AgwSessionStream(framing, portByte, localCallsign, remoteCallsign, logger, Watch);
                        return new AgwConnection(tcp, sessionStream, framing, portByte, localCallsign, remoteCallsign, logger, crossed.Task);
                    }
                }

                if (frame.Kind == 'd')
                {
                    var msg = Encoding.ASCII.GetString(frame.Payload).TrimEnd('\0', '\r', '\n');
                    throw new IOException($"AGW connect to {remoteCallsign} failed: {msg}");
                }

                Watch(frame);
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A monitored frame that is the peer's SABM to us, direct, on the port
    /// we're calling it on. BPQ's monitor shows that as
    /// " 1:Fm PEER To US &lt;C C P&gt;[12:00:00]" (a connect, as a command).
    /// A SABM through digipeaters (" Via ...") or a SABME ("&lt;?? C P&gt;")
    /// prints differently and isn't counted: the session then falls back
    /// to waiting out the prompt.
    /// </summary>
    internal static bool IsPeerCallingUs(AgwFrame monitored, byte port, string localCallsign, string remoteCallsign) =>
        monitored.Port == port
        && Encoding.ASCII.GetString(monitored.Payload).Contains(
            $":Fm {remoteCallsign} To {localCallsign} <C C", StringComparison.OrdinalIgnoreCase);

    /// <summary>Frame text as it can go in a log: printable ASCII only.</summary>
    private static string Printable(byte[] payload) =>
        new([.. Encoding.Latin1.GetString(payload).Where(c => c is >= ' ' and <= '~')]);

    private sealed class AgwConnection(
        TcpClient tcp,
        AgwSessionStream sessionStream,
        AgwFrameTransport framing,
        byte portByte,
        string localCallsign,
        string remoteCallsign,
        ILogger logger,
        Task crossedCall) : IDappsConnection
    {
        public Stream Stream => sessionStream;

        public Task CrossedCall => crossedCall;

        /// <summary>Close the socket without a 'd': the link is a newer
        /// session's now.</summary>
        public ValueTask AbandonAsync()
        {
            sessionStream.Dispose();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            // Send a 'd' (disconnect) frame so BPQ tears down the AX.25
            // session immediately rather than waiting for the link's idle
            // timeout. Without this, a follow-up connect from the same
            // callsign pair within a few minutes collides with the stale
            // half-up link - surfaced repeatedly in integration runs.
            //
            // Not once the node has told us the link is gone, though. BPQ
            // applies a 'd' to whichever session it has for the callsign
            // pair and port, so after ours has gone a 'd' could only find
            // a newer one, such as our listener's next call from the peer.
            if (sessionStream.RemoteDisconnected)
            {
                logger.LogDebug("AGW: the link to {0} has already gone; no 'd' to send", remoteCallsign);
            }
            else
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await framing.WriteFrameAsync(
                        new AgwFrame(portByte, 'd', 0, localCallsign, remoteCallsign, []),
                        cts.Token);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "AGW: best-effort 'd' frame on dispose failed");
                }
            }
            sessionStream.Dispose();
            tcp.Dispose();
        }
    }
}
