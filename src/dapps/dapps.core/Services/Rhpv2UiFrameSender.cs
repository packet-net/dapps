using System.Globalization;
using dapps.client.Discovery;
using dapps.client.Tx;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.core.Services;

/// <summary>
/// Sends AGW discovery's beacons and solicits through the node's RHPv2
/// port while the AGW connection only listens (RHPv2 is the bearer, so it
/// registers no callsign there: Tom's decision for #272). XRouter's AGW
/// drops an 'M' from a socket that registered no callsign without a word,
/// so the beacons have to go this way; hearing stays on the AGW monitor.
///
/// The frame goes out on the channel's port by the node's own number for
/// it (XRouter's <c>PORT=</c>), which RHPv2 takes as it is. The discovery
/// bearer has already checked that the AGW connection hears that same
/// port (<see cref="NodePortNumbers"/>, which reads XRouter's port numbers
/// from its RHPv2 port once per AGW connection): with any other program
/// answering AGW (Dire Wolf or BPQ next to pdn, say) nothing ties its
/// ports to the RHPv2 host's, and nothing is sent.
///
/// Each frame takes a short connection of its own, as the RHPv2 outbound
/// transport does for a call: a datagram socket bound to our callsign, one
/// <c>sendto</c>, closed. XRouter lets one datagram socket bind the
/// callsign the listener holds on a port (but only one at a time: a second
/// is "Duplicate socket"), so frames go one after another.
/// </summary>
public sealed class Rhpv2UiFrameSender(
    IOptionsMonitor<SystemOptions> options,
    ILogger<Rhpv2UiFrameSender> logger,
    IDappsTxGate? txGate = null) : IUiFrameSender
{
    /// <summary>The longest one frame may take, from the TCP connect to
    /// the node's answer to the <c>sendto</c>.</summary>
    public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(10);

    internal TimeSpan SendTimeout { get; init; } = DefaultSendTimeout;

    private readonly IDappsTxGate txGate = txGate ?? AlwaysOpenTxGate.Instance;
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    public async Task SendUiAsync(int portNumber, string from, string to, byte[] payload, CancellationToken ct)
    {
        if (!txGate.TxAllowed)
        {
            throw new TxStoppedException(
                $"RHP UI frame {from}->{to} on port {portNumber}: {txGate.BlockReason ?? "(no reason)"}");
        }
        if (portNumber < 1)
        {
            throw new UiFrameNotSentException($"RHP: not sending on port {portNumber}: the node's ports count from 1");
        }
        var portName = portNumber.ToString(CultureInfo.InvariantCulture);

        var opts = options.CurrentValue;
        var host = string.IsNullOrEmpty(opts.NodeHost) ? "127.0.0.1" : opts.NodeHost;
        var port = opts.RhpPort > 0 ? opts.RhpPort : RhpClient.DefaultPort;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await oneAtATime.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UiFrameNotSentException(
                $"RHP: the UI frame {from}->{to} on port {portNumber} waited {SendTimeout.TotalSeconds:F0}s behind another; not sent");
        }

        var issued = false;
        try
        {
            await using var rhp = await RhpClient.ConnectAsync(host, port, timeout.Token);
            if (!string.IsNullOrEmpty(opts.RhpUser))
            {
                await rhp.AuthenticateAsync(opts.RhpUser, opts.RhpPass ?? "", timeout.Token);
            }

            if (!txGate.TxAllowed)
            {
                throw new TxStoppedException(
                    $"RHP UI frame {from}->{to} on RHPv2 port {portName}: {txGate.BlockReason ?? "(no reason)"}");
            }

            var handle = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Dgram, timeout.Token);
            try
            {
                await rhp.BindAsync(handle, local: from, port: portName, timeout.Token);
                issued = true;
                await rhp.SendToAsync(handle, RhpDataEncoding.ToWireString(payload),
                    port: portName, local: from, remote: to, ct: timeout.Token);
            }
            finally
            {
                try { await rhp.CloseAsync(handle, timeout.Token); }
                catch { /* the connection closes next, and the handle with it */ }
            }
            logger.LogDebug("RHP: UI frame {0}->{1} sent on RHPv2 port {2}", from, to, portName);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var text = $"RHP: {host}:{port} did not answer within {SendTimeout.TotalSeconds:F0}s";
            // Once the sendto has gone the frame may be on air: charged.
            if (issued) throw new IOException($"{text}; the UI frame {from}->{to} on port {portNumber} may have gone");
            throw new UiFrameNotSentException($"{text}; the UI frame {from}->{to} on port {portNumber} was not sent");
        }
        catch (RhpServerException ex)
        {
            throw new UiFrameNotSentException(
                $"RHP: {host}:{port} refused the UI frame {from}->{to} on port {portNumber} ({ex.Message}); not sent", ex);
        }
        catch (Exception ex) when (!issued && ex is System.Net.Sockets.SocketException or IOException and not UiFrameNotSentException)
        {
            throw new UiFrameNotSentException(
                $"RHP: could not reach {host}:{port} ({ex.Message}); the UI frame {from}->{to} on port {portNumber} was not sent", ex);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

}
