using System.Text;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.core.Services;

/// <summary>Types one command at the local node's own command prompt and
/// returns what it answers. Nothing goes on air.</summary>
public interface INodePrompt
{
    /// <summary>The node's reply to <paramref name="command"/>. Throws
    /// <see cref="NodePromptUnavailableException"/> saying why when the
    /// node can't be asked.</summary>
    Task<string> AskAsync(string command, CancellationToken ct);
}

/// <summary>The node's prompt can't be reached; the message says why.
/// <see cref="ForGood"/> when asking again won't help: the node has no way
/// to its prompt at all.</summary>
public sealed class NodePromptUnavailableException(string message, Exception? inner = null, bool forGood = false) : Exception(message, inner)
{
    public bool ForGood { get; } = forGood;
}

/// <summary>
/// Reaches the local node's own command prompt, for the node check
/// (<see cref="NodeDiagnostics"/>, #425), in whichever way the node has:
///
/// - LinBPQ over AGW: a session to <c>SWITCH-15</c> on DAPPS's one AGW
///   connection (<see cref="AgwLink.OpenNodeSessionAsync"/>), only once the
///   node's 'R' answer says it is LinBPQ. It sits in the link's session
///   table like any other, so inbound calls and our own sessions are
///   untouched (#272).
/// - XRouter, over either bearer: an RHPv2 <c>open</c> in the <c>unix</c>
///   family, which lands at its prompt ("Connected to SWITCH"). On AGW,
///   XRouter is told by its port list, as <see cref="NodePortNumbers"/> does.
/// - pdn has no way to its prompt over RHPv2 (it refuses the <c>unix</c>
///   family), and an AGW host that is neither LinBPQ nor XRouter is not
///   tried: a 'C' to SWITCH would go on air there.
/// </summary>
public sealed class NodePrompt(
    IOptionsMonitor<SystemOptions> options,
    IAgwLinkSource agwLinks,
    ILogger<NodePrompt> logger) : INodePrompt
{
    /// <summary>Longest the node may take to open the session.</summary>
    internal static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(10);

    /// <summary>Longest to wait for the first of the reply.</summary>
    internal static readonly TimeSpan ReplyWait = TimeSpan.FromSeconds(8);

    /// <summary>The reply is taken as complete after this long with nothing more.</summary>
    internal static readonly TimeSpan QuietGap = TimeSpan.FromMilliseconds(1500);

    // One question at a time: the AGW session's key is fixed.
    private readonly SemaphoreSlim one = new(1, 1);

    public async Task<string> AskAsync(string command, CancellationToken ct)
    {
        if (!await one.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            throw new NodePromptUnavailableException("another question to the node is still waiting for its answer");
        }
        try
        {
            return await AskOnceAsync(command, ct);
        }
        finally
        {
            one.Release();
        }
    }

    private async Task<string> AskOnceAsync(string command, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.Callsign))
        {
            throw new NodePromptUnavailableException("no callsign is set");
        }
        if (string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase))
        {
            return await AskOverRhpAsync(opts, command, ct);
        }

        var link = agwLinks.TryGetLink() ?? throw new NodePromptUnavailableException("the AGW connection to the node isn't open");
        if (link.ListenOnly) throw new NodePromptUnavailableException("the AGW connection only listens for discovery beacons");

        (int Major, int Minor) version;
        try
        {
            version = await link.QueryVersionAsync(ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            throw new NodePromptUnavailableException($"the node didn't say which AGW host it is ({ex.Message})", ex);
        }
        if (version == AgwLink.LinbpqVersion) return await AskOverAgwAsync(link, command, ct);

        AgwPortList ports;
        try
        {
            ports = await link.QueryPortsAsync(ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            throw new NodePromptUnavailableException($"the node didn't answer a port query ({ex.Message})", ex);
        }
        if (NodePortNumbers.XrouterAgwPortCount(ports.Payload) is not null)
        {
            return await AskOverRhpAsync(opts, command, ct);
        }
        throw new NodePromptUnavailableException(
            $"the AGW host (version {version.Major}.{version.Minor}) is neither LinBPQ nor XRouter, so DAPPS has no way to its prompt", forGood: true);
    }

    private async Task<string> AskOverAgwAsync(AgwLink link, string command, CancellationToken ct)
    {
        AgwDial session;
        try
        {
            session = await link.OpenNodeSessionAsync(OpenWait, ct, logger);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or NotSupportedException or InvalidOperationException)
        {
            throw new NodePromptUnavailableException($"LinBPQ didn't open a session at its prompt ({ex.Message})", ex);
        }
        await using (session)
        {
            var stream = session.Stream;
            var reply = new StringBuilder();
            var buffer = new byte[1024];
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r"), ct);
            await stream.FlushAsync(ct);
            await CollectAsync(async token =>
            {
                var n = await stream.ReadAsync(buffer, token);
                if (n == 0) return null;
                return Encoding.Latin1.GetString(buffer, 0, n);
            }, reply, ct);
            return reply.ToString();
        }
    }

    private async Task<string> AskOverRhpAsync(SystemOptions opts, string command, CancellationToken ct)
    {
        var host = string.IsNullOrEmpty(opts.NodeHost) ? "127.0.0.1" : opts.NodeHost;
        var port = opts.RhpPort > 0 ? opts.RhpPort : RhpClient.DefaultPort;
        RhpClient rhp;
        using (var opening = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            opening.CancelAfter(OpenWait);
            try
            {
                rhp = await RhpClient.ConnectAsync(host, port, opening.Token);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                throw new NodePromptUnavailableException($"couldn't connect to the node's RHPv2 port {host}:{port} ({ex.Message})", ex);
            }
        }

        await using (rhp)
        {
            // Everything that arrives, by handle, from before the open:
            // the node may greet straight behind its reply.
            var arrived = System.Threading.Channels.Channel.CreateUnbounded<(int Handle, string Text)>();
            rhp.Received += (_, e) =>
                arrived.Writer.TryWrite((e.Message.Handle, Encoding.Latin1.GetString(RhpDataEncoding.FromWireString(e.Message.Data ?? ""))));

            int handle;
            using (var opening = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                opening.CancelAfter(OpenWait);
                if (!string.IsNullOrEmpty(opts.RhpUser))
                {
                    try
                    {
                        await rhp.AuthenticateAsync(opts.RhpUser, opts.RhpPass ?? "", opening.Token);
                    }
                    catch (RhpServerException ex)
                    {
                        throw new NodePromptUnavailableException($"the node refused DAPPS's RHPv2 login ({ex.Message})", ex);
                    }
                }
                try
                {
                    var reply = await rhp.OpenWithReplyAsync(
                        family: ProtocolFamily.Unix,
                        mode: SocketMode.Stream,
                        port: null,
                        local: opts.Callsign,
                        remote: "SWITCH",
                        flags: OpenFlags.Active,
                        ct: opening.Token);
                    handle = reply.Handle;
                }
                catch (RhpServerException ex) when (ex.ErrorCode == RhpErrorCode.OperationNotSupported)
                {
                    // pdn: "family 'unix' is not implemented yet (ax25 only)".
                    throw new NodePromptUnavailableException($"the node's RHPv2 has no way to its prompt (pdn has none: {ex.Message})", ex, forGood: true);
                }
                catch (RhpServerException ex)
                {
                    throw new NodePromptUnavailableException($"the node refused a session at its prompt ({ex.Message})", ex);
                }
                catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    throw new NodePromptUnavailableException($"the node's RHPv2 port didn't open a session at its prompt ({ex.Message})", ex);
                }
            }

            try
            {
                // XRouter greets with "Connected to SWITCH": let it pass.
                var greeting = new StringBuilder();
                await CollectAsync(token => NextAsync(arrived.Reader, handle, token), greeting, ct, firstWait: TimeSpan.FromSeconds(2), gap: TimeSpan.FromMilliseconds(500));

                await rhp.SendOnHandleAsync(handle, command + "\r", ct);
                var text = new StringBuilder();
                await CollectAsync(token => NextAsync(arrived.Reader, handle, token), text, ct);
                return text.ToString();
            }
            finally
            {
                try
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await rhp.CloseAsync(handle, closing.Token);
                }
                catch
                {
                    // The connection's end closes it anyway.
                }
            }
        }
    }

    private static async Task<string?> NextAsync(System.Threading.Channels.ChannelReader<(int Handle, string Text)> reader, int handle, CancellationToken ct)
    {
        while (true)
        {
            var (h, text) = await reader.ReadAsync(ct);
            if (h == handle) return text;
        }
    }

    /// <summary>
    /// Appends what <paramref name="read"/> gives to <paramref name="into"/>
    /// until nothing more has come for <paramref name="gap"/> (once
    /// something has), nothing at all for <paramref name="firstWait"/>, the
    /// session ends (null), or 15 s in all.
    /// </summary>
    internal static async Task CollectAsync(
        Func<CancellationToken, Task<string?>> read, StringBuilder into, CancellationToken ct,
        TimeSpan? firstWait = null, TimeSpan? gap = null)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(15));
        var wait = firstWait ?? ReplyWait;
        while (true)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            step.CancelAfter(wait);
            string? chunk;
            try
            {
                chunk = await read(step.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return;
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return;
            }
            if (chunk is null) return;
            into.Append(chunk);
            wait = gap ?? QuietGap;
        }
    }
}
