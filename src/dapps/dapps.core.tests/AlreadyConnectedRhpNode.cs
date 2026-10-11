using System.Net;
using System.Net.Sockets;
using RhpV2.Client.Protocol;

namespace dapps.core.tests;

/// <summary>
/// An RHPv2 node that refuses every open with pdn's own error 18, "Already
/// connected", as pdn (node-v0.65.0 on) does when something at the node
/// already holds the link to the peer: the peer's call to us, or a console
/// or another app. Takes any number of client connections, as DAPPS opens a
/// fresh one per dial; counts the opens, and runs <see cref="OnOpen"/> for
/// each before it answers.
/// </summary>
internal sealed class AlreadyConnectedRhpNode : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task serving;
    private int opens;

    public AlreadyConnectedRhpNode()
    {
        listener.Start();
        serving = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int Opens => Volatile.Read(ref opens);

    public Action<OpenMessage>? OnOpen { get; set; }

    private async Task AcceptAsync()
    {
        var clients = new List<Task>();
        try
        {
            while (true) clients.Add(ServeAsync(await listener.AcceptTcpClientAsync(stop.Token)));
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
        try { await Task.WhenAll(clients); } catch { /* a client went mid-frame */ }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        using (tcp)
        {
            var s = tcp.GetStream();
            try
            {
                while (await RhpFraming.ReadFrameAsync(s, stop.Token) is { } frame)
                {
                    if (RhpJson.Deserialize(frame) is not OpenMessage open) continue;
                    Interlocked.Increment(ref opens);
                    OnOpen?.Invoke(open);
                    var reply = new OpenReplyMessage
                    {
                        Id = open.Id, Handle = 0, ErrCode = 18, ErrText = $"Already connected to {open.Remote} from {open.Local}.",
                    };
                    var write = new MemoryStream();
                    RhpFraming.WriteFrame(write, RhpJson.Serialize(reply));
                    await s.WriteAsync(write.ToArray(), stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client hung up, or the node stopped.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try { await serving; } catch { /* stopping */ }
        stop.Dispose();
    }
}
