using System.Buffers.Binary;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;

namespace dapps.meshcore;

/// <summary>
/// Client for the MeshCore "Companion" USB-serial protocol (firmware v1.16.x).
/// Framing: host→device <c>[0x3C][len_lo][len_hi][payload]</c>,
/// device→host <c>[0x3E][len_lo][len_hi][payload]</c> (len = LE uint16, payload
/// only; first payload byte is the opcode). Device→host frames are synchronous
/// responses (code &lt; 0x80) or async pushes (≥0x80). Inbound over-the-air
/// messages are queued: the device emits a 1-byte MSG_WAITING (0x83) tickle and
/// the host pulls each with SYNC_NEXT_MESSAGE (0x0A). 8N1 @ 115200; DTR/RTS held
/// low so opening the port does not reset the board.
/// </summary>
public sealed class MeshCoreClient : IAsyncDisposable
{
    // command codes (host → device)
    public const byte CMD_APP_START = 0x01;
    public const byte CMD_SEND_CHANNEL_TXT_MSG = 0x03;
    public const byte CMD_SET_ADVERT_NAME = 0x08;
    public const byte CMD_SYNC_NEXT_MESSAGE = 0x0A;
    public const byte CMD_SET_RADIO_PARAMS = 0x0B;
    public const byte CMD_SET_RADIO_TX_POWER = 0x0C;
    public const byte CMD_GET_CHANNEL = 0x1F;
    public const byte CMD_SET_CHANNEL = 0x20;
    public const byte CMD_SEND_CHANNEL_DATA = 0x3E;
    public const ushort DATA_TYPE_DEV = 0xFFFF;

    // response codes (device → host, synchronous)
    public const byte RSP_OK = 0x00;
    public const byte RSP_ERR = 0x01;
    public const byte RSP_SELF_INFO = 0x05;
    public const byte RSP_NO_MORE_MESSAGES = 0x0A;
    public const byte RSP_CONTACT_MSG_RECV = 0x07;     // legacy (no SNR)
    public const byte RSP_CHANNEL_MSG_RECV = 0x08;     // legacy (no SNR) - what v1.16 sends for channel text
    public const byte RSP_CONTACT_MSG_RECV_V3 = 0x10;
    public const byte RSP_CHANNEL_MSG_RECV_V3 = 0x11;
    public const byte RSP_CHANNEL_INFO = 0x12;
    public const byte RSP_CHANNEL_DATA_RECV = 0x1B;    // binary channel datagram

    // push codes (device → host, async)
    public const byte PUSH_SEND_CONFIRMED = 0x82;
    public const byte PUSH_MSG_WAITING = 0x83;
    public const byte PUSH_LOG_RX_DATA = 0x88;        // a packet was heard on the channel

    private const byte FrameToRadio = 0x3C;
    private const byte FrameFromRadio = 0x3E;

    public static readonly bool Trace = Environment.GetEnvironmentVariable("MESHCORE_TRACE") == "1";

    private readonly SerialPort _port;
    private readonly Channel<byte[]> _responses =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    /// <summary>UTC time of the last successful request/response exchange. The
    /// watchdog uses this to spot a hung radio.</summary>
    public DateTime LastOkUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Raised when the device signals queued inbound messages (0x83).</summary>
    public event Action? MessageWaiting;

    /// <summary>Raised for every packet the radio overhears on the channel
    /// (LOG_RX_DATA 0x88) — args are (logged length, snr*4). Feeds channel-
    /// occupancy estimation (#157).</summary>
    public event Action<int, sbyte>? PacketHeard;

    public MeshCoreClient(string portName, int baud = 115200)
    {
        _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 200,
            WriteTimeout = 2000,
        };
    }

    public void Open()
    {
        _port.Open();
        try { _port.DtrEnable = false; _port.RtsEnable = false; } catch { /* best effort */ }
        _port.DiscardInBuffer();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _port.BaseStream;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int marker = await ReadByteAsync(stream, ct);
                if (marker is not (FrameFromRadio or FrameToRadio)) continue;
                int lo = await ReadByteAsync(stream, ct);
                int hi = await ReadByteAsync(stream, ct);
                if (lo < 0 || hi < 0) continue;
                int len = lo | (hi << 8);
                if (len is < 0 or > 4096) continue;
                var payload = await ReadExactAsync(stream, len, ct);
                if (payload is null || payload.Length == 0) continue;

                byte code = payload[0];
                if (Trace)
                    Console.Error.WriteLine($"<< {payload.Length}B code=0x{code:X2} {Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 48)))}");
                if (code >= 0x80) HandlePush(code, payload);
                else _responses.Writer.TryWrite(payload);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* keep the loop alive across transient serial hiccups */ }
        }
    }

    private void HandlePush(byte code, byte[] payload)
    {
        switch (code)
        {
            case PUSH_MSG_WAITING:
                MessageWaiting?.Invoke();
                break;
            case PUSH_LOG_RX_DATA:
                // payload is the logged RX record; first byte after the opcode is
                // SNR in the RX-frame family. Length is a proxy for on-air size.
                var snr = payload.Length > 1 ? unchecked((sbyte)payload[1]) : (sbyte)0;
                PacketHeard?.Invoke(payload.Length, snr);
                break;
        }
    }

    /// <summary>Send a command frame and await the next synchronous response whose
    /// opcode is in <paramref name="expectedCodes"/> (skipping unrelated frames).</summary>
    public async Task<byte[]> ExchangeAsync(byte[] payload, byte[] expectedCodes, TimeSpan timeout, CancellationToken ct)
    {
        await _exchange.WaitAsync(ct);
        try
        {
            while (_responses.Reader.TryRead(out _)) { }
            WriteFrame(payload);

            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(timeout);
            while (true)
            {
                byte[] resp;
                try { resp = await _responses.Reader.ReadAsync(to.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"no response (codes {Hex(expectedCodes)}) within {timeout.TotalSeconds:0.#}s");
                }
                if (Array.IndexOf(expectedCodes, resp[0]) >= 0)
                {
                    LastOkUtc = DateTime.UtcNow;
                    return resp;
                }
                if (resp[0] == RSP_ERR)
                    throw new MeshCoreException($"device returned ERR code {(resp.Length > 1 ? resp[1] : 0)}");
            }
        }
        finally { _exchange.Release(); }
    }

    public void WriteFrame(ReadOnlySpan<byte> payload)
    {
        var buf = new byte[3 + payload.Length];
        buf[0] = FrameToRadio;
        buf[1] = (byte)(payload.Length & 0xFF);
        buf[2] = (byte)((payload.Length >> 8) & 0xFF);
        payload.CopyTo(buf.AsSpan(3));
        if (Trace)
            Console.Error.WriteLine($">> {payload.Length}B code=0x{payload[0]:X2} {Convert.ToHexString(buf.AsSpan(0, Math.Min(buf.Length, 48)))}");
        _port.BaseStream.Write(buf, 0, buf.Length);
        _port.BaseStream.Flush();
    }

    // ---------- device control ----------

    public async Task<SelfInfo> AppStartAsync(string appName, CancellationToken ct)
    {
        var p = new List<byte> { CMD_APP_START, 0x03, 0, 0, 0, 0, 0, 0 };
        p.AddRange(Encoding.ASCII.GetBytes(appName));
        // Retry: opening the port can reset the board and race the first APP_START.
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var resp = await ExchangeAsync(p.ToArray(), [RSP_SELF_INFO], TimeSpan.FromSeconds(3), ct);
                return SelfInfo.Parse(resp);
            }
            catch (TimeoutException ex) { last = ex; await Task.Delay(800, ct); }
        }
        throw last ?? new TimeoutException("APP_START failed");
    }

    public async Task SetNameAsync(string name, CancellationToken ct)
    {
        var p = new byte[1 + Encoding.UTF8.GetByteCount(name)];
        p[0] = CMD_SET_ADVERT_NAME;
        Encoding.UTF8.GetBytes(name).CopyTo(p, 1);
        await ExchangeAsync(p, [RSP_OK], TimeSpan.FromSeconds(3), ct);
    }

    public async Task SetRadioParamsAsync(double freqMhz, double bwKhz, byte sf, byte cr, CancellationToken ct)
    {
        var p = new byte[11];
        p[0] = CMD_SET_RADIO_PARAMS;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1, 4), (uint)Math.Round(freqMhz * 1000));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(5, 4), (uint)Math.Round(bwKhz * 1000));
        p[9] = sf;
        p[10] = cr;
        await ExchangeAsync(p, [RSP_OK], TimeSpan.FromSeconds(3), ct);
    }

    public async Task SetTxPowerAsync(byte dbm, CancellationToken ct) =>
        await ExchangeAsync([CMD_SET_RADIO_TX_POWER, dbm], [RSP_OK], TimeSpan.FromSeconds(3), ct);

    public async Task SetChannelAsync(byte index, string name, byte[] secret16, CancellationToken ct)
    {
        if (secret16.Length != 16) throw new ArgumentException("secret must be 16 bytes", nameof(secret16));
        var p = new byte[1 + 1 + 32 + 16];
        p[0] = CMD_SET_CHANNEL;
        p[1] = index;
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Array.Copy(nameBytes, 0, p, 2, Math.Min(nameBytes.Length, 32));
        Array.Copy(secret16, 0, p, 34, 16);
        await ExchangeAsync(p, [RSP_OK], TimeSpan.FromSeconds(3), ct);
    }

    public async Task<ChannelInfo> GetChannelAsync(byte index, CancellationToken ct)
    {
        var resp = await ExchangeAsync([CMD_GET_CHANNEL, index], [RSP_CHANNEL_INFO], TimeSpan.FromSeconds(3), ct);
        return ChannelInfo.Parse(resp);
    }

    /// <summary>Send a binary datagram to a channel (flood). Bytes arrive verbatim
    /// at the peer (no name prefix). Payload ≤ 165.</summary>
    public async Task SendChannelDataAsync(byte channelIndex, byte[] payload, ushort dataType, CancellationToken ct)
    {
        if (payload.Length > 165) throw new ArgumentException("channel-data payload must be <= 165 bytes", nameof(payload));
        var p = new byte[5 + payload.Length];
        p[0] = CMD_SEND_CHANNEL_DATA;
        p[1] = channelIndex;
        p[2] = 0xFF;                                  // path_len: flood
        p[3] = (byte)(dataType & 0xFF);
        p[4] = (byte)(dataType >> 8);
        payload.CopyTo(p, 5);
        await ExchangeAsync(p, [RSP_OK], TimeSpan.FromSeconds(5), ct);
    }

    public sealed record InboundBatch(List<ChannelMessage> Texts, List<ChannelData> Data);

    /// <summary>Drain all queued inbound messages (text + binary).</summary>
    public async Task<InboundBatch> DrainAsync(CancellationToken ct)
    {
        var texts = new List<ChannelMessage>();
        var data = new List<ChannelData>();
        while (true)
        {
            byte[] resp;
            try
            {
                resp = await ExchangeAsync(
                    [CMD_SYNC_NEXT_MESSAGE],
                    [RSP_CHANNEL_MSG_RECV, RSP_CHANNEL_MSG_RECV_V3, RSP_CHANNEL_DATA_RECV,
                     RSP_CONTACT_MSG_RECV, RSP_CONTACT_MSG_RECV_V3, RSP_NO_MORE_MESSAGES],
                    TimeSpan.FromMilliseconds(1500), ct);
            }
            catch (TimeoutException) { break; }
            if (resp[0] == RSP_NO_MORE_MESSAGES) return new InboundBatch(texts, data);
            try
            {
                switch (resp[0])
                {
                    case RSP_CHANNEL_MSG_RECV: texts.Add(ChannelMessage.ParseLegacy(resp)); break;
                    case RSP_CHANNEL_MSG_RECV_V3: texts.Add(ChannelMessage.ParseV3(resp)); break;
                    case RSP_CHANNEL_DATA_RECV: data.Add(ChannelData.ParseRecv(resp)); break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                // Skip one malformed inbound frame; keep draining the rest of the queue.
                if (Trace) Console.Error.WriteLine($"drain: skipping malformed 0x{resp[0]:X2} frame: {ex.Message}");
            }
        }
        return new InboundBatch(texts, data);
    }

    private static async Task<int> ReadByteAsync(Stream s, CancellationToken ct)
    {
        var b = new byte[1];
        try { return await s.ReadAsync(b.AsMemory(0, 1), ct) == 1 ? b[0] : -1; }
        catch (TimeoutException) { return -1; }
        catch (IOException) { return -1; }
    }

    private static async Task<byte[]?> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var got = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (got < count)
        {
            if (DateTime.UtcNow > deadline) return null;
            int n;
            try { n = await s.ReadAsync(buf.AsMemory(got, count - got), ct); }
            catch (TimeoutException) { continue; }
            catch (IOException) { return null; }
            if (n == 0) continue;
            got += n;
        }
        return buf;
    }

    private static string Hex(byte[] b) => string.Join(",", b.Select(x => "0x" + x.ToString("X2")));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_readLoop is not null) { try { await _readLoop; } catch { } }
        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
        _cts.Dispose();
    }
}

public sealed class MeshCoreException(string message) : Exception(message);
