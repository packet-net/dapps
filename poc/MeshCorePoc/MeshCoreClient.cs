using System.Buffers.Binary;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;

namespace MeshCorePoc;

/// <summary>
/// Minimal client for the MeshCore "Companion" USB-serial protocol
/// (firmware v1.16.0). Independent of DAPPS. Framing:
///   host -> device:  [0x3C][len_lo][len_hi][payload...]
///   device -> host:  [0x3E][len_lo][len_hi][payload...]
/// len is a little-endian uint16 counting payload bytes only. The first
/// payload byte is the opcode. Device-to-host frames are either
/// synchronous responses (code &lt; 0x80) or asynchronous pushes (code
/// &gt;= 0x80). Inbound over-the-air messages are NOT pushed inline: the
/// device emits a 1-byte MSG_WAITING (0x83) tickle and the host pulls
/// each queued message with SYNC_NEXT_MESSAGE (0x0A).
/// 8N1 @ 115200, no flow control. DTR/RTS held low so opening the port
/// does not reset the board.
/// </summary>
public sealed class MeshCoreClient : IAsyncDisposable
{
    // ---- command codes (host -> device) ----
    public const byte CMD_APP_START = 0x01;
    public const byte CMD_SEND_CHANNEL_TXT_MSG = 0x03;
    public const byte CMD_SET_ADVERT_NAME = 0x08;
    public const byte CMD_SYNC_NEXT_MESSAGE = 0x0A;
    public const byte CMD_SET_RADIO_PARAMS = 0x0B;
    public const byte CMD_SET_RADIO_TX_POWER = 0x0C;
    public const byte CMD_GET_CHANNEL = 0x1F;
    public const byte CMD_SET_CHANNEL = 0x20;
    public const byte CMD_SEND_CHANNEL_DATA = 0x3E;    // binary group datagram
    public const ushort DATA_TYPE_DEV = 0xFFFF;        // developer namespace

    // ---- response codes (device -> host, synchronous) ----
    public const byte RSP_OK = 0x00;
    public const byte RSP_ERR = 0x01;
    public const byte RSP_SELF_INFO = 0x05;
    public const byte RSP_NO_MORE_MESSAGES = 0x0A;
    public const byte RSP_CONTACT_MSG_RECV = 0x07;     // legacy (no SNR)
    public const byte RSP_CHANNEL_MSG_RECV = 0x08;     // legacy (no SNR) - what v1.16.0 actually sends for channels
    public const byte RSP_CONTACT_MSG_RECV_V3 = 0x10;
    public const byte RSP_CHANNEL_MSG_RECV_V3 = 0x11;
    public const byte RSP_CHANNEL_INFO = 0x12;
    public const byte RSP_CHANNEL_DATA_RECV = 0x1B;    // binary group datagram

    // ---- push codes (device -> host, async, high bit set) ----
    public const byte PUSH_SEND_CONFIRMED = 0x82;
    public const byte PUSH_MSG_WAITING = 0x83;

    private const byte FrameToRadio = 0x3C;   // '<'
    private const byte FrameFromRadio = 0x3E; // '>'

    /// <summary>Set MESHCORE_TRACE=1 to log every frame in/out to stderr.</summary>
    public static readonly bool Trace = Environment.GetEnvironmentVariable("MESHCORE_TRACE") == "1";

    private readonly SerialPort _port;
    // Synchronous response frames only (code < 0x80). Pushes are handled
    // inline in the read loop and never land here.
    private readonly Channel<byte[]> _responses =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    // One command/response exchange at a time.
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    /// <summary>Raised when the device signals queued inbound messages (0x83).</summary>
    public event Action? MessageWaiting;

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
        // Hold the auto-reset lines low; some adapters assert on open.
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
                // Resync: scan for a frame marker.
                int marker = await ReadByteAsync(stream, ct);
                if (marker < 0) continue;
                if (marker != FrameFromRadio && marker != FrameToRadio) continue;

                int lo = await ReadByteAsync(stream, ct);
                int hi = await ReadByteAsync(stream, ct);
                if (lo < 0 || hi < 0) continue;
                int len = lo | (hi << 8);
                if (len is < 0 or > 4096) continue; // resync guard

                var payload = await ReadExactAsync(stream, len, ct);
                if (payload is null || payload.Length == 0) continue;

                byte code = payload[0];
                if (Trace)
                    Console.Error.WriteLine($"<< {payload.Length}B code=0x{code:X2} {Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 48)))}");
                if (code >= 0x80)
                {
                    HandlePush(code, payload);
                }
                else
                {
                    _responses.Writer.TryWrite(payload);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* keep the loop alive; transient serial hiccup */ }
        }
    }

    private void HandlePush(byte code, byte[] payload)
    {
        switch (code)
        {
            case PUSH_MSG_WAITING:
                MessageWaiting?.Invoke();
                break;
            // PUSH_SEND_CONFIRMED and others are ignored for the PoC.
        }
    }

    /// <summary>
    /// Send a command frame and await the next synchronous response whose
    /// opcode is in <paramref name="expectedCodes"/>. Frames with other
    /// (non-push) opcodes are skipped, so a stray boot frame can't be
    /// mistaken for our reply.
    /// </summary>
    public async Task<byte[]> ExchangeAsync(byte[] payload, byte[] expectedCodes, TimeSpan timeout, CancellationToken ct)
    {
        await _exchange.WaitAsync(ct);
        try
        {
            // Clear any stale buffered responses before issuing the command.
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
                if (Array.IndexOf(expectedCodes, resp[0]) >= 0) return resp;
                if (resp[0] == RSP_ERR)
                    throw new MeshCoreException($"device returned ERR code {(resp.Length > 1 ? resp[1] : 0)}");
                // else: unexpected non-push frame; skip and keep waiting.
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

    // ---------- high-level operations ----------

    public async Task<SelfInfo> AppStartAsync(string appName, CancellationToken ct)
    {
        // [0]=0x01 [1]=proto ver(3) [2..7]=6 reserved zeros [8..]=app name
        var p = new List<byte> { CMD_APP_START, 0x03, 0, 0, 0, 0, 0, 0 };
        p.AddRange(Encoding.ASCII.GetBytes(appName));
        var resp = await ExchangeAsync(p.ToArray(), [RSP_SELF_INFO], TimeSpan.FromSeconds(4), ct);
        return SelfInfo.Parse(resp);
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

    public async Task SetTxPowerAsync(byte dbm, CancellationToken ct)
    {
        await ExchangeAsync([CMD_SET_RADIO_TX_POWER, dbm], [RSP_OK], TimeSpan.FromSeconds(3), ct);
    }

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

    /// <summary>Send a UTF-8 text message to a channel slot. Returns when
    /// the device acks (OK) that it queued the frame for transmission.</summary>
    public async Task SendChannelTextAsync(byte channelIndex, string text, uint unixTime, CancellationToken ct)
    {
        var textBytes = Encoding.UTF8.GetBytes(text);
        var p = new byte[7 + textBytes.Length];
        p[0] = CMD_SEND_CHANNEL_TXT_MSG;
        p[1] = 0;             // txt_type = PLAIN
        p[2] = channelIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(3, 4), unixTime);
        textBytes.CopyTo(p, 7);
        await ExchangeAsync(p, [RSP_OK], TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>Send a binary datagram to a channel (SEND_CHANNEL_DATA 0x3E).
    /// Flood (path_len=0xFF). data_type defaults to the DEV namespace. The bytes
    /// arrive byte-identical at the peer (no name prefix). Payload &lt;= 165.</summary>
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

    /// <summary>All inbound items drained from one pull cycle.</summary>
    public sealed record InboundBatch(List<ChannelMessage> Texts, List<ChannelData> Data);

    /// <summary>Drain all queued inbound messages (text + binary). Pulls with
    /// SYNC_NEXT_MESSAGE until NO_MORE_MESSAGES.</summary>
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
            catch (TimeoutException)
            {
                // Some firmware stays silent on an empty queue rather than
                // replying NO_MORE_MESSAGES; treat that as "nothing waiting".
                break;
            }
            switch (resp[0])
            {
                case RSP_NO_MORE_MESSAGES: return new InboundBatch(texts, data);
                case RSP_CHANNEL_MSG_RECV: texts.Add(ChannelMessage.ParseLegacy(resp)); break;
                case RSP_CHANNEL_MSG_RECV_V3: texts.Add(ChannelMessage.ParseV3(resp)); break;
                case RSP_CHANNEL_DATA_RECV: data.Add(ChannelData.ParseRecv(resp)); break;
                // CONTACT_MSG_RECV* (direct messages) ignored for the PoC.
            }
        }
        return new InboundBatch(texts, data);
    }

    private static async Task<int> ReadByteAsync(Stream s, CancellationToken ct)
    {
        var b = new byte[1];
        try
        {
            int n = await s.ReadAsync(b.AsMemory(0, 1), ct);
            return n == 1 ? b[0] : -1;
        }
        catch (TimeoutException) { return -1; }
        catch (IOException) { return -1; }
    }

    private static async Task<byte[]?> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int got = 0;
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
        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* ignore */ }
        }
        try { if (_port.IsOpen) _port.Close(); } catch { /* ignore */ }
        _port.Dispose();
        _cts.Dispose();
    }
}

public sealed class MeshCoreException(string message) : Exception(message);
