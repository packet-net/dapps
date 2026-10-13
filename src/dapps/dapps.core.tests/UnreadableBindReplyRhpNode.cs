using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RhpV2.Client.Protocol;

namespace dapps.core.tests;

/// <summary>
/// An RHPv2 node that answers bind as XRouter 505o does, with a reply that
/// isn't JSON (see <c>Rhpv2Bind</c>), and socket, listen and close as any
/// node does. The bind reply carries the error code and text it is made
/// with, unquoted and unlabelled as 505o sends them. Records the requests
/// it gets.
/// </summary>
internal sealed class UnreadableBindReplyRhpNode : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task serving;
    private readonly int bindErrCode;
    private readonly string bindErrText;
    private int nextHandle;

    public UnreadableBindReplyRhpNode(int bindErrCode = 0, string bindErrText = "Ok")
    {
        this.bindErrCode = bindErrCode;
        this.bindErrText = bindErrText;
        listener.Start();
        serving = Task.Run(AcceptAsync);
    }

    public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;

    public ConcurrentQueue<RhpMessage> Requests { get; } = new();

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
                    var request = RhpJson.Deserialize(frame);
                    Requests.Enqueue(request);
                    var reply = request switch
                    {
                        SocketMessage m => Ok("socketReply", m.Id, Interlocked.Increment(ref nextHandle)),
                        BindMessage m => $$"""{"type": "bind", "id": {{m.Id}}, "handle": {{m.Handle}}, "errCode": {{bindErrCode}}, {{bindErrText}}}""",
                        ListenMessage m => Ok("listenReply", m.Id, m.Handle),
                        CloseMessage m => Ok("closeReply", m.Id, m.Handle),
                        _ => null,
                    };
                    if (reply is null) continue;
                    var write = new MemoryStream();
                    RhpFraming.WriteFrame(write, Encoding.UTF8.GetBytes(reply));
                    await s.WriteAsync(write.ToArray(), stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client hung up, or the node stopped.
            }
        }
    }

    private static string Ok(string type, int? id, int? handle) =>
        $$"""{"type": "{{type}}", "id": {{id}}, "handle": {{handle}}, "errCode": 0, "errText": "Ok"}""";

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try { await serving; } catch { /* stopping */ }
        stop.Dispose();
    }
}
