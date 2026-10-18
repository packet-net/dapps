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
/// Each frame takes a short connection of its own, as the RHPv2 outbound
/// transport does for a call: a datagram socket bound to our callsign on
/// the port, one <c>sendto</c>, then closed. Both XRouter and pdn send a
/// datagram from the bound callsign with PID 0xF0, and XRouter lets one
/// datagram socket bind the callsign the listener holds (but only one at a
/// time: a second bind of it is "Duplicate socket"), so frames go one
/// after another.
/// </summary>
public sealed class Rhpv2UiFrameSender(
    IOptionsMonitor<SystemOptions> options,
    ILogger<Rhpv2UiFrameSender> logger,
    IDappsTxGate? txGate = null) : IUiFrameSender
{
    /// <summary>The longest one frame may take, from the TCP connect to
    /// the node's answer to the <c>sendto</c>.</summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly IDappsTxGate txGate = txGate ?? AlwaysOpenTxGate.Instance;
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    public async Task SendUiAsync(byte bearerPort, string from, string to, byte[] payload, CancellationToken ct)
    {
        // DAPPS counts ports from 0, RHPv2 names them from "1"
        // (Rhpv2OutboundTransport).
        var portName = (bearerPort + 1).ToString(CultureInfo.InvariantCulture);
        if (!txGate.TxAllowed)
        {
            throw new TxStoppedException(
                $"RHP UI frame {from}->{to} on port {portName}: {txGate.BlockReason ?? "(no reason)"}");
        }

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
            throw new IOException(
                $"RHP: the UI frame {from}->{to} on port {portName} waited {SendTimeout.TotalSeconds:F0}s behind another; not sent");
        }
        try
        {
            await using var rhp = await RhpClient.ConnectAsync(host, port, timeout.Token);
            if (!string.IsNullOrEmpty(opts.RhpUser))
            {
                await rhp.AuthenticateAsync(opts.RhpUser, opts.RhpPass ?? "", timeout.Token);
            }
            var handle = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Dgram, timeout.Token);
            try
            {
                await rhp.BindAsync(handle, local: from, port: portName, timeout.Token);
                await rhp.SendToAsync(handle, RhpDataEncoding.ToWireString(payload),
                    port: portName, local: from, remote: to, ct: timeout.Token);
            }
            finally
            {
                try { await rhp.CloseAsync(handle, timeout.Token); }
                catch { /* the connection closes next, and the handle with it */ }
            }
            logger.LogDebug("RHP: UI frame {0}->{1} sent on port {2}", from, to, portName);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(
                $"RHP: {host}:{port} did not take the UI frame {from}->{to} on port {portName} within {SendTimeout.TotalSeconds:F0}s; not sent");
        }
        catch (RhpServerException ex)
        {
            throw new IOException(
                $"RHP: {host}:{port} refused the UI frame {from}->{to} on port {portName} ({ex.Message}); not sent", ex);
        }
        finally
        {
            oneAtATime.Release();
        }
    }
}
