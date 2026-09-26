using System.Net.Sockets;
using System.Text;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// A bare AX.25 caller through a real node's AGW port, reading and writing
/// lines by hand: how a test talks DAPPSv1 to a daemon without a DAPPS
/// client in the way, to check the server's replies one by one.
/// </summary>
internal sealed class RawAgwCaller : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly AgwFrameTransport agw;
    private readonly byte port;
    private readonly string from;
    private readonly string to;
    private readonly List<byte> received = [];
    private readonly SemaphoreSlim arrived = new(0);
    private readonly CancellationTokenSource stop = new();
    private volatile bool disconnected;

    private RawAgwCaller(TcpClient tcp, byte port, string from, string to)
    {
        this.tcp = tcp;
        agw = new AgwFrameTransport(tcp.GetStream());
        this.port = port;
        this.from = from;
        this.to = to;
    }

    public bool Disconnected => disconnected;

    public static async Task<RawAgwCaller> ConnectAsync(string host, int agwPort, string from, string to, int bearerPort, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, agwPort, ct);
        var caller = new RawAgwCaller(tcp, (byte)bearerPort, from, to);
        await caller.agw.WriteFrameAsync(new AgwFrame(0, 'X', 0, from, "", []), ct);
        while ((await caller.agw.ReadFrameAsync(ct)).Kind != 'X') { }
        await caller.agw.WriteFrameAsync(new AgwFrame(caller.port, 'C', 0, from, to, []), ct);
        while ((await caller.agw.ReadFrameAsync(ct)).Kind != 'C') { }
        _ = caller.PumpAsync();
        return caller;
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var frame = await agw.ReadFrameAsync(stop.Token);
                if (frame.Kind == 'D')
                {
                    lock (received) received.AddRange(frame.Payload);
                    arrived.Release();
                }
                else if (frame.Kind == 'd')
                {
                    disconnected = true;
                    arrived.Release();
                }
            }
        }
        catch
        {
            disconnected = true;
            arrived.Release();
        }
    }

    public Task SendAsync(string text, CancellationToken ct) => SendAsync(Encoding.UTF8.GetBytes(text), ct);

    public Task SendAsync(byte[] bytes, CancellationToken ct) => agw.WriteDataAsync(port, from, to, bytes, ct);

    /// <summary>The next line the far end sent, or null if it hung up first.</summary>
    public async Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (received)
            {
                var nl = received.IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    var line = Encoding.UTF8.GetString(received.GetRange(0, nl).ToArray()).TrimEnd('\r');
                    received.RemoveRange(0, nl + 1);
                    return line;
                }
            }
            if (disconnected) return null;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException("no line from the far end");
            await arrived.WaitAsync(left, ct);
        }
    }

    /// <summary>Exactly <paramref name="count"/> bytes: a payload after its <c>data</c> line.</summary>
    public async Task<byte[]> ReadBytesAsync(int count, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (received)
            {
                if (received.Count >= count)
                {
                    var bytes = received.GetRange(0, count).ToArray();
                    received.RemoveRange(0, count);
                    return bytes;
                }
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || disconnected) throw new TimeoutException($"wanted {count} bytes");
            await arrived.WaitAsync(left, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!disconnected)
        {
            try { await agw.WriteFrameAsync(new AgwFrame(port, 'd', 0, from, to, []), CancellationToken.None); } catch { }
            await Task.Delay(1000);
        }
        stop.Cancel();
        tcp.Dispose();
    }
}
