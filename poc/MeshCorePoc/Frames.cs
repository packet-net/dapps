using System.Buffers.Binary;
using System.Text;

namespace MeshCorePoc;

/// <summary>Parsed SELF_INFO (0x05) reply to APP_START.</summary>
public sealed record SelfInfo(
    byte AdvType, byte TxPower, byte MaxTxPower, byte[] PublicKey,
    double FreqMhz, double BwKhz, byte Sf, byte Cr, string Name)
{
    public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();

    public static SelfInfo Parse(byte[] p)
    {
        // [0]=0x05 [1]=adv_type [2]=tx_power [3]=max_tx [4..36]=pubkey(32)
        // [48..52]=freq*1000 [52..56]=bw*1000 [56]=sf [57]=cr [58..]=name
        var pub = p[4..36];
        double freq = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(48, 4)) / 1000.0;
        double bw = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(52, 4)) / 1000.0;
        byte sf = p[56], cr = p[57];
        string name = p.Length > 58
            ? Encoding.UTF8.GetString(p, 58, p.Length - 58).TrimEnd('\0')
            : "";
        return new SelfInfo(p[1], p[2], p[3], pub, freq, bw, sf, cr, name);
    }
}

/// <summary>Parsed CHANNEL_INFO (0x12) reply to GET_CHANNEL.</summary>
public sealed record ChannelInfo(byte Index, string Name, byte[] Secret)
{
    public string SecretHex => Convert.ToHexString(Secret).ToLowerInvariant();

    public static ChannelInfo Parse(byte[] p)
    {
        // [0]=0x12 [1]=index [2..34]=name(32) [34..50]=secret(16)
        byte idx = p[1];
        string name = Encoding.UTF8.GetString(p, 2, 32).TrimEnd('\0');
        var secret = p[34..50];
        return new ChannelInfo(idx, name, secret);
    }
}

/// <summary>Parsed CHANNEL_MSG_RECV_V3 (0x11) inbound channel message.</summary>
public sealed record ChannelMessage(
    sbyte Snr, byte ChannelIndex, byte PathLen, byte TxtType, uint Timestamp, string Text)
{
    /// <summary>SNR in dB (firmware sends snr*4).</summary>
    public double SnrDb => Snr / 4.0;
    /// <summary>0xFF path-len means the packet was received direct (no flood hops).</summary>
    public bool ReceivedDirect => PathLen == 0xFF;

    public static ChannelMessage ParseV3(byte[] p)
    {
        // [0]=0x11 [1]=snr(int8) [2..3]=reserved [4]=channel_idx [5]=path_len
        // [6]=txt_type [7..10]=timestamp u32 LE [11..]=text
        sbyte snr = unchecked((sbyte)p[1]);
        byte ch = p[4], pathLen = p[5], txtType = p[6];
        uint ts = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(7, 4));
        string text = p.Length > 11 ? Encoding.UTF8.GetString(p, 11, p.Length - 11) : "";
        return new ChannelMessage(snr, ch, pathLen, txtType, ts, text);
    }

    public static ChannelMessage ParseLegacy(byte[] p)
    {
        // [0]=0x08 [1]=channel_idx [2]=path_len [3]=txt_type [4..7]=timestamp
        // u32 LE [8..]=text. No SNR field (SnrDb reports 0 = unknown).
        byte ch = p[1], pathLen = p[2], txtType = p[3];
        uint ts = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4, 4));
        string text = p.Length > 8 ? Encoding.UTF8.GetString(p, 8, p.Length - 8) : "";
        return new ChannelMessage(0, ch, pathLen, txtType, ts, text);
    }
}

/// <summary>Parsed CHANNEL_DATA_RECV (0x1B) inbound binary channel datagram.</summary>
public sealed record ChannelData(sbyte Snr, byte ChannelIndex, byte PathLen, ushort DataType, byte[] Payload)
{
    public double SnrDb => Snr / 4.0;
    public bool ReceivedDirect => PathLen == 0xFF;

    public static ChannelData ParseRecv(byte[] p)
    {
        // [0]=0x1B [1]=snr(int8 x4) [2..3]=reserved [4]=channel_idx [5]=path_len
        // [6..7]=data_type u16 LE [8]=data_len [9..]=payload
        sbyte snr = unchecked((sbyte)p[1]);
        byte ch = p[4], pathLen = p[5];
        ushort dataType = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6, 2));
        byte dataLen = p[8];
        var payload = p.Length >= 9 + dataLen ? p[9..(9 + dataLen)] : p[9..];
        return new ChannelData(snr, ch, pathLen, dataType, payload);
    }
}
