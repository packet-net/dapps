using System.Globalization;
using System.Text.RegularExpressions;
using dapps.client.Discovery;
using dapps.client.Transport.Agw;
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
/// The frame must go out on the radio port the channel hears on, and the
/// two protocols number ports differently. XRouter's AGW counts its ports
/// from 0 in order of their <c>PORT=</c> numbers, whatever order they are
/// written in, and its answer to a 'G' says <c>Port1 with "id"</c> for the
/// first; RHPv2 names them by the <c>PORT=</c> number itself. With ports
/// 1, 3 and 4, AGW port 2 is RHPv2 port "4", not "3". So before each frame
/// the sender asks the node which RHPv2 port numbers exist (a datagram
/// bind to a number that is not a port is refused, "Invalid port", and
/// nothing goes on air), takes the one at the AGW port's position, and
/// sends there. It sends nothing, and says why, unless the AGW answer is
/// XRouter's and the node has exactly as many RHPv2 ports as AGW ports:
/// with any other program answering AGW (Dire Wolf or BPQ next to pdn, say)
/// nothing ties its ports to the RHPv2 host's.
///
/// Each frame takes a short connection of its own, as the RHPv2 outbound
/// transport does for a call: datagram sockets bound to our callsign, one
/// <c>sendto</c>, each closed. XRouter lets one datagram socket bind the
/// callsign the listener holds on a port (but only one at a time: a second
/// is "Duplicate socket"), so frames go one after another.
/// </summary>
public sealed partial class Rhpv2UiFrameSender(
    IOptionsMonitor<SystemOptions> options,
    ILogger<Rhpv2UiFrameSender> logger,
    IDappsTxGate? txGate = null) : IUiFrameSender
{
    /// <summary>The longest one frame may take, from the TCP connect to
    /// the node's answer to the <c>sendto</c>.</summary>
    public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The highest RHPv2 port number asked about.</summary>
    internal const int MaxPortNumber = 64;

    private const int InvalidPort = 10;

    internal TimeSpan SendTimeout { get; init; } = DefaultSendTimeout;

    private readonly IDappsTxGate txGate = txGate ?? AlwaysOpenTxGate.Instance;
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    public async Task SendUiAsync(byte agwPort, string from, string to, byte[] payload, AgwPortList? agwPorts, CancellationToken ct)
    {
        if (!txGate.TxAllowed)
        {
            throw new TxStoppedException(
                $"RHP UI frame {from}->{to} on AGW port {agwPort}: {txGate.BlockReason ?? "(no reason)"}");
        }
        var agwCount = XrouterAgwPortCount(agwPorts?.Payload)
            ?? throw new UiFrameNotSentException(
                $"RHP: not sending on AGW port {agwPort}: the AGW port list is not XRouter's, so its ports cannot be matched to the RHPv2 host's");
        if (agwPort >= agwCount)
        {
            throw new UiFrameNotSentException(
                $"RHP: not sending on AGW port {agwPort}: the node has {agwCount} AGW port(s)");
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
            throw new UiFrameNotSentException(
                $"RHP: the UI frame {from}->{to} on AGW port {agwPort} waited {SendTimeout.TotalSeconds:F0}s behind another; not sent");
        }

        var issued = false;
        try
        {
            await using var rhp = await RhpClient.ConnectAsync(host, port, timeout.Token);
            if (!string.IsNullOrEmpty(opts.RhpUser))
            {
                await rhp.AuthenticateAsync(opts.RhpUser, opts.RhpPass ?? "", timeout.Token);
            }

            var numbers = await RhpPortNumbersAsync(rhp, from, agwCount, timeout.Token);
            if (numbers.Count != agwCount)
            {
                throw new UiFrameNotSentException(
                    $"RHP: not sending on AGW port {agwPort}: the node has {agwCount} AGW port(s) but RHPv2 port(s) [{string.Join(",", numbers)}] up to {MaxPortNumber}, so they cannot be matched");
            }
            var portName = numbers[agwPort].ToString(CultureInfo.InvariantCulture);
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
            logger.LogDebug("RHP: UI frame {0}->{1} sent on RHPv2 port {2} (AGW port {3})", from, to, portName, agwPort);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var text = $"RHP: {host}:{port} did not answer within {SendTimeout.TotalSeconds:F0}s";
            // Once the sendto has gone the frame may be on air: charged.
            if (issued) throw new IOException($"{text}; the UI frame {from}->{to} on AGW port {agwPort} may have gone");
            throw new UiFrameNotSentException($"{text}; the UI frame {from}->{to} on AGW port {agwPort} was not sent");
        }
        catch (RhpServerException ex)
        {
            throw new UiFrameNotSentException(
                $"RHP: {host}:{port} refused the UI frame {from}->{to} on AGW port {agwPort} ({ex.Message}); not sent", ex);
        }
        catch (Exception ex) when (!issued && ex is System.Net.Sockets.SocketException or IOException and not UiFrameNotSentException)
        {
            throw new UiFrameNotSentException(
                $"RHP: could not reach {host}:{port} ({ex.Message}); the UI frame {from}->{to} on AGW port {agwPort} was not sent", ex);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    /// <summary>The RHPv2 port numbers that exist, lowest first, up to
    /// <paramref name="enough"/> of them: a datagram bind to each number
    /// in turn, which the node refuses with "Invalid port" for a number
    /// that is not one of its ports. A bind transmits nothing.</summary>
    private static async Task<List<int>> RhpPortNumbersAsync(RhpClient rhp, string callsign, int enough, CancellationToken ct)
    {
        var found = new List<int>();
        for (var n = 1; n <= MaxPortNumber && found.Count < enough; n++)
        {
            var probe = await rhp.SocketAsync(ProtocolFamily.Ax25, SocketMode.Dgram, ct);
            try
            {
                await rhp.BindAsync(probe, local: callsign, port: n.ToString(CultureInfo.InvariantCulture), ct);
                found.Add(n);
            }
            catch (RhpServerException ex) when (ex.ErrorCode == InvalidPort)
            {
            }
            finally
            {
                try { await rhp.CloseAsync(probe, ct); }
                catch (RhpServerException) { /* gone already */ }
            }
        }
        return found;
    }

    /// <summary>The number of ports in XRouter's answer to a 'G'
    /// (<c>3;Port1 with "id";Port2 with "id";Port3 with "id";</c>), or
    /// null if it is not that shape: BPQ, Dire Wolf and others describe
    /// their ports in other words, and their port numbers say nothing
    /// about an RHPv2 host's.</summary>
    internal static int? XrouterAgwPortCount(byte[]? payload)
    {
        if (payload is null) return null;
        var parts = AgwPortQuery.Parse(payload);
        if (parts.Count == 0) return null;
        var text = System.Text.Encoding.ASCII.GetString(payload).TrimEnd('\0').Trim();
        var first = text.Split(';')[0];
        if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count != parts.Count) return null;
        for (var i = 0; i < parts.Count; i++)
        {
            var m = XrouterPortDescription().Match(parts[i].Description);
            if (!m.Success || m.Groups[1].Value != (i + 1).ToString(CultureInfo.InvariantCulture)) return null;
        }
        return count;
    }

    [GeneratedRegex("^Port([0-9]+) with \"[^\"]*\"$")]
    private static partial Regex XrouterPortDescription();
}
