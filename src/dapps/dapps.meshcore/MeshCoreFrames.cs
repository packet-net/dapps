using System.Buffers.Binary;
using System.Text;

namespace dapps.meshcore;

/// <summary>Parsed SELF_INFO (0x05) reply to APP_START.</summary>
public sealed record SelfInfo(
    byte AdvType, byte TxPower, byte MaxTxPower, byte[] PublicKey,
    double FreqMhz, double BwKhz, byte Sf, byte Cr, string Name)
{
    public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();

    public static SelfInfo Parse(byte[] p)
    {
        if (p.Length < 58) throw new InvalidDataException($"SELF_INFO frame too short ({p.Length} bytes)");
        var pub = p[4..36];
        double freq = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(48, 4)) / 1000.0;
        double bw = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(52, 4)) / 1000.0;
        byte sf = p[56], cr = p[57];
        string name = p.Length > 58 ? Encoding.UTF8.GetString(p, 58, p.Length - 58).TrimEnd('\0') : "";
        return new SelfInfo(p[1], p[2], p[3], pub, freq, bw, sf, cr, name);
    }
}

/// <summary>Parsed CHANNEL_INFO (0x12) reply to GET_CHANNEL.</summary>
public sealed record ChannelInfo(byte Index, string Name, byte[] Secret)
{
    public string SecretHex => Convert.ToHexString(Secret).ToLowerInvariant();

    public static ChannelInfo Parse(byte[] p)
    {
        if (p.Length < 50) throw new InvalidDataException($"CHANNEL_INFO frame too short ({p.Length} bytes)");
        byte idx = p[1];
        // The firmware returns a null-terminated name in a 32-byte field whose
        // tail is uninitialised; trim at the first null.
        var nameField = p.AsSpan(2, 32);
        var nul = nameField.IndexOf((byte)0);
        string name = Encoding.UTF8.GetString(nul >= 0 ? nameField[..nul] : nameField);
        return new ChannelInfo(idx, name, p[34..50]);
    }
}

/// <summary>Parsed inbound channel TEXT message (legacy 0x08 or V3 0x11).</summary>
public sealed record ChannelMessage(
    sbyte Snr, byte ChannelIndex, byte PathLen, byte TxtType, uint Timestamp, string Text)
{
    public double SnrDb => Snr / 4.0;
    public bool ReceivedDirect => PathLen == 0xFF;

    public static ChannelMessage ParseV3(byte[] p)
    {
        if (p.Length < 11) throw new InvalidDataException($"CHANNEL_MSG_RECV_V3 frame too short ({p.Length} bytes)");
        sbyte snr = unchecked((sbyte)p[1]);
        byte ch = p[4], pathLen = p[5], txtType = p[6];
        uint ts = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(7, 4));
        string text = p.Length > 11 ? Encoding.UTF8.GetString(p, 11, p.Length - 11) : "";
        return new ChannelMessage(snr, ch, pathLen, txtType, ts, text);
    }

    public static ChannelMessage ParseLegacy(byte[] p)
    {
        if (p.Length < 8) throw new InvalidDataException($"CHANNEL_MSG_RECV frame too short ({p.Length} bytes)");
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
        if (p.Length < 9) throw new InvalidDataException($"CHANNEL_DATA_RECV frame too short ({p.Length} bytes)");
        sbyte snr = unchecked((sbyte)p[1]);
        byte ch = p[4], pathLen = p[5];
        ushort dataType = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6, 2));
        byte dataLen = p[8];
        var payload = p.Length >= 9 + dataLen ? p[9..(9 + dataLen)] : p[9..];
        return new ChannelData(snr, ch, pathLen, dataType, payload);
    }
}
