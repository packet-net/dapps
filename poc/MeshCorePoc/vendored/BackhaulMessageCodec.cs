using System.Buffers.Binary;
using System.Text;

namespace dapps.client.Backhaul.Datagram;

/// <summary>
/// Self-describing binary codec for <see cref="BackhaulMessage"/>. Used
/// by datagram-shaped bearers (UDP today, MeshCore Companion / KISS
/// later) where the streamed DAPPSv1 <c>ihave</c>/<c>data</c> exchange
/// doesn't fit. Stays in <c>dapps.client</c> so any bearer impl can
/// reuse it without taking a dependency on the AGW path.
///
/// Wire format (all integers little-endian). The version byte is the
/// first thing on the wire; receivers reject anything other than the
/// current <see cref="Version"/>. We're pre-shipping, so there's no
/// in-flight traffic that would break - the version mechanism is
/// preserved (so a future format change still hard-fails cleanly
/// rather than silently misinterpreting bytes), but we don't carry
/// historical decoder paths.
/// <code>
///   [1]  version           = current Version
///   [2]  flags (UInt16)    bit0=salt, bit1=ttl, bit2=headers,
///                          bit3=originator, bit4=link-source,
///                          bit5=flood-hops-remaining,
///                          bit6=source-route, bit7=traversed-hops,
///                          bit8=fragment (F2 multi-part),
///                          bit9=stream (opt-in ordering)
///   [7]  id (UTF-8 ASCII, 7-char hex from DappsMessage.ComputeHash)
///   [8]  salt              (only when flags bit0)
///   [4]  ttl seconds       (only when flags bit1)
///   [2]  destination len
///   [N]  destination       (UTF-8)
///   [2]  originator len    (only when flags bit3)
///   [O]  originator        (UTF-8)
///   [2]  link-source len   (only when flags bit4)
///   [L]  link-source       (UTF-8)
///   [1]  flood-hops        (only when flags bit5)
///   [1]  source-route count (only when flags bit6)
///   per source-route hop:
///     [1] hop len, [N] hop (UTF-8)
///   [1]  traversed count   (only when flags bit7)
///   per traversed hop:
///     [1] hop len, [N] hop (UTF-8)
///   [7]  master id         (only when flags bit8; ASCII)
///   [2]  fragment index    (only when flags bit8; UInt16, 1-based)
///   [2]  fragment total    (only when flags bit8; UInt16)
///   [1]  stream id len     (only when flags bit9; max 255 bytes)
///   [S]  stream id         (only when flags bit9; UTF-8)
///   [4]  stream seq        (only when flags bit9; UInt32 LE)
///   [4]  stream gap timeout (only when flags bit9; UInt32 LE seconds, 0=strict)
///   [2]  headers count     (only when flags bit2)
///   per header:
///     [2] key len, [K] key (UTF-8), [2] value len, [V] value (UTF-8)
///   [4]  payload len
///   [P]  payload bytes
/// </code>
/// Length-prefixed throughout - no escapes, binary-safe.
/// </summary>
public static class BackhaulMessageCodec
{
    /// <summary>Version this encoder writes AND the only version the
    /// decoder accepts. Bump on any wire-format change so a mismatched
    /// peer fails fast instead of silently misreading flag bits.</summary>
    public const byte Version = 7;
    public const int IdLength = 7;

    [Flags]
    private enum Flags : ushort
    {
        None = 0,
        HasSalt = 1 << 0,
        HasTtl = 1 << 1,
        HasHeaders = 1 << 2,
        HasOriginator = 1 << 3,
        HasLinkSource = 1 << 4,
        HasFloodHops = 1 << 5,
        HasSourceRoute = 1 << 6,
        HasTraversedHops = 1 << 7,
        HasFragment = 1 << 8,
        HasStream = 1 << 9,
    }

    public static byte[] Encode(BackhaulMessage message)
    {
        if (message.Id.Length != IdLength)
        {
            throw new ArgumentException($"id must be exactly {IdLength} characters; got '{message.Id}'", nameof(message));
        }

        // F2 fragment headers always travel together; presence of MasterId
        // is the gate. Validate the trio so an in-transit relay can't half-
        // strip them (which would leave the receiver unable to reassemble
        // and unable to tell that's what happened).
        var hasFragment = message.MasterId is not null;
        if (hasFragment)
        {
            if (message.MasterId!.Length != IdLength)
            {
                throw new ArgumentException($"master id must be exactly {IdLength} characters; got '{message.MasterId}'", nameof(message));
            }
            if (!message.FragmentIndex.HasValue || !message.FragmentTotal.HasValue)
            {
                throw new ArgumentException("fragment index/total must accompany master id", nameof(message));
            }
        }
        else if (message.FragmentIndex.HasValue || message.FragmentTotal.HasValue)
        {
            throw new ArgumentException("fragment index/total without master id", nameof(message));
        }

        // Opt-in ordering trio: present together or absent together. The
        // receiver enforces this in IHaveValidator too, but a relay that
        // forwards a partial set would silently drop ordering for the
        // downstream hop, which is worse than failing fast here.
        var hasStream = !string.IsNullOrEmpty(message.StreamId)
            || message.StreamSeq.HasValue
            || message.StreamGapTimeoutSeconds.HasValue;
        if (hasStream
            && (string.IsNullOrEmpty(message.StreamId)
                || !message.StreamSeq.HasValue
                || !message.StreamGapTimeoutSeconds.HasValue))
        {
            throw new ArgumentException(
                "stream id/seq/gap-timeout must all be set together (opt-in ordering) or all be absent",
                nameof(message));
        }
        var streamIdBytes = hasStream ? Encoding.UTF8.GetBytes(message.StreamId!) : [];
        if (hasStream && streamIdBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException("stream id exceeds 255 bytes", nameof(message));
        }

        var idBytes = Encoding.ASCII.GetBytes(message.Id);
        var dstBytes = Encoding.UTF8.GetBytes(message.Destination);
        var origBytes = string.IsNullOrEmpty(message.Originator)
            ? []
            : Encoding.UTF8.GetBytes(message.Originator!);
        var linkBytes = string.IsNullOrEmpty(message.LinkSourceCallsign)
            ? []
            : Encoding.UTF8.GetBytes(message.LinkSourceCallsign!);
        var headerBytes = message.Headers is { Count: > 0 }
            ? EncodeHeaders(message.Headers)
            : [];
        var sourceRouteBytes = message.SourceRoute is { Count: > 0 }
            ? EncodeCallsignList(message.SourceRoute)
            : [];
        var traversedBytes = message.TraversedHops is { Count: > 0 }
            ? EncodeCallsignList(message.TraversedHops)
            : [];
        var masterIdBytes = hasFragment
            ? Encoding.ASCII.GetBytes(message.MasterId!)
            : [];

        var flags = Flags.None;
        if (message.Salt.HasValue) flags |= Flags.HasSalt;
        if (message.Ttl.HasValue) flags |= Flags.HasTtl;
        if (headerBytes.Length > 0) flags |= Flags.HasHeaders;
        if (origBytes.Length > 0) flags |= Flags.HasOriginator;
        if (linkBytes.Length > 0) flags |= Flags.HasLinkSource;
        if (message.FloodHopsRemaining.HasValue) flags |= Flags.HasFloodHops;
        if (sourceRouteBytes.Length > 0) flags |= Flags.HasSourceRoute;
        if (traversedBytes.Length > 0) flags |= Flags.HasTraversedHops;
        if (hasFragment) flags |= Flags.HasFragment;
        if (hasStream) flags |= Flags.HasStream;

        var size = 1 + 2 + IdLength
            + (message.Salt.HasValue ? 8 : 0)
            + (message.Ttl.HasValue ? 4 : 0)
            + 2 + dstBytes.Length
            + (origBytes.Length > 0 ? 2 + origBytes.Length : 0)
            + (linkBytes.Length > 0 ? 2 + linkBytes.Length : 0)
            + (message.FloodHopsRemaining.HasValue ? 1 : 0)
            + sourceRouteBytes.Length
            + traversedBytes.Length
            + (hasFragment ? IdLength + 2 + 2 : 0)
            + (hasStream ? 1 + streamIdBytes.Length + 4 + 4 : 0)
            + headerBytes.Length
            + 4 + message.Payload.Length;

        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)flags);
        offset += 2;

        idBytes.CopyTo(buffer.AsSpan(offset));
        offset += IdLength;

        if (message.Salt.HasValue)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), message.Salt.Value);
            offset += 8;
        }
        if (message.Ttl.HasValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), message.Ttl.Value);
            offset += 4;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)dstBytes.Length);
        offset += 2;
        dstBytes.CopyTo(buffer.AsSpan(offset));
        offset += dstBytes.Length;

        if (origBytes.Length > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)origBytes.Length);
            offset += 2;
            origBytes.CopyTo(buffer.AsSpan(offset));
            offset += origBytes.Length;
        }

        if (linkBytes.Length > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)linkBytes.Length);
            offset += 2;
            linkBytes.CopyTo(buffer.AsSpan(offset));
            offset += linkBytes.Length;
        }

        if (message.FloodHopsRemaining.HasValue)
        {
            buffer[offset++] = message.FloodHopsRemaining.Value;
        }

        if (sourceRouteBytes.Length > 0)
        {
            sourceRouteBytes.CopyTo(buffer.AsSpan(offset));
            offset += sourceRouteBytes.Length;
        }

        if (traversedBytes.Length > 0)
        {
            traversedBytes.CopyTo(buffer.AsSpan(offset));
            offset += traversedBytes.Length;
        }

        if (hasFragment)
        {
            masterIdBytes.CopyTo(buffer.AsSpan(offset));
            offset += IdLength;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)message.FragmentIndex!.Value);
            offset += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)message.FragmentTotal!.Value);
            offset += 2;
        }

        if (hasStream)
        {
            buffer[offset++] = (byte)streamIdBytes.Length;
            streamIdBytes.CopyTo(buffer.AsSpan(offset));
            offset += streamIdBytes.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), message.StreamSeq!.Value);
            offset += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), message.StreamGapTimeoutSeconds!.Value);
            offset += 4;
        }

        headerBytes.CopyTo(buffer.AsSpan(offset));
        offset += headerBytes.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)message.Payload.Length);
        offset += 4;
        message.Payload.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static BackhaulMessage Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 1 + 2 + IdLength + 2 + 4)
        {
            throw new InvalidDataException("buffer too short for any valid backhaul message");
        }

        var offset = 0;
        var version = buffer[offset++];
        if (version != Version)
        {
            throw new InvalidDataException($"unsupported codec version {version}; expected {Version}");
        }
        var flags = (Flags)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
        offset += 2;

        var id = Encoding.ASCII.GetString(buffer.Slice(offset, IdLength));
        offset += IdLength;

        long? salt = null;
        if ((flags & Flags.HasSalt) != 0)
        {
            salt = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
            offset += 8;
        }

        int? ttl = null;
        if ((flags & Flags.HasTtl) != 0)
        {
            ttl = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
            offset += 4;
        }

        var dstLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
        offset += 2;
        var destination = Encoding.UTF8.GetString(buffer.Slice(offset, dstLen));
        offset += dstLen;

        string? originator = null;
        if ((flags & Flags.HasOriginator) != 0)
        {
            var origLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
            offset += 2;
            originator = Encoding.UTF8.GetString(buffer.Slice(offset, origLen));
            offset += origLen;
        }

        string? linkSource = null;
        if ((flags & Flags.HasLinkSource) != 0)
        {
            var linkLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
            offset += 2;
            linkSource = Encoding.UTF8.GetString(buffer.Slice(offset, linkLen));
            offset += linkLen;
        }

        byte? floodHops = null;
        if ((flags & Flags.HasFloodHops) != 0)
        {
            floodHops = buffer[offset++];
        }

        IReadOnlyList<string>? sourceRoute = null;
        if ((flags & Flags.HasSourceRoute) != 0)
        {
            sourceRoute = DecodeCallsignList(buffer, ref offset);
        }

        IReadOnlyList<string>? traversedHops = null;
        if ((flags & Flags.HasTraversedHops) != 0)
        {
            traversedHops = DecodeCallsignList(buffer, ref offset);
        }

        string? masterId = null;
        int? fragmentIndex = null;
        int? fragmentTotal = null;
        if ((flags & Flags.HasFragment) != 0)
        {
            masterId = Encoding.ASCII.GetString(buffer.Slice(offset, IdLength));
            offset += IdLength;
            fragmentIndex = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
            offset += 2;
            fragmentTotal = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
            offset += 2;
        }

        string? streamId = null;
        uint? streamSeq = null;
        uint? streamGapTimeout = null;
        if ((flags & Flags.HasStream) != 0)
        {
            var sidLen = buffer[offset++];
            streamId = Encoding.UTF8.GetString(buffer.Slice(offset, sidLen));
            offset += sidLen;
            streamSeq = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
            offset += 4;
            streamGapTimeout = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
            offset += 4;
        }

        IReadOnlyDictionary<string, string>? headers = null;
        if ((flags & Flags.HasHeaders) != 0)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
            offset += 2;
            var dict = new Dictionary<string, string>(count);
            for (var i = 0; i < count; i++)
            {
                var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
                offset += 2;
                var key = Encoding.UTF8.GetString(buffer.Slice(offset, keyLen));
                offset += keyLen;
                var valLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
                offset += 2;
                var val = Encoding.UTF8.GetString(buffer.Slice(offset, valLen));
                offset += valLen;
                dict[key] = val;
            }
            headers = dict;
        }

        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;
        var payload = buffer.Slice(offset, (int)payloadLen).ToArray();

        return new BackhaulMessage(id, destination, salt, ttl, payload, headers, originator, linkSource, floodHops, sourceRoute, traversedHops, masterId, fragmentIndex, fragmentTotal, streamId, streamSeq, streamGapTimeout);
    }

    private static byte[] EncodeHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var pairs = headers.Select(kv => (Key: Encoding.UTF8.GetBytes(kv.Key), Val: Encoding.UTF8.GetBytes(kv.Value))).ToArray();
        var size = 2 + pairs.Sum(p => 2 + p.Key.Length + 2 + p.Val.Length);
        var buf = new byte[size];
        var off = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), (ushort)pairs.Length);
        off += 2;
        foreach (var (key, val) in pairs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), (ushort)key.Length);
            off += 2;
            key.CopyTo(buf.AsSpan(off));
            off += key.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), (ushort)val.Length);
            off += 2;
            val.CopyTo(buf.AsSpan(off));
            off += val.Length;
        }
        return buf;
    }

    /// <summary>Length-prefixed list of UTF-8 callsigns. Count is one
    /// byte (max 255 hops - far beyond anything realistic; AODV-style
    /// algorithms cap at ~10) and each hop's length is one byte (max
    /// 255 chars; a callsign is ~9 incl SSID).</summary>
    private static byte[] EncodeCallsignList(IReadOnlyList<string> hops)
    {
        if (hops.Count > byte.MaxValue)
        {
            throw new ArgumentException($"callsign list exceeds {byte.MaxValue} entries", nameof(hops));
        }
        var encoded = hops.Select(h => Encoding.UTF8.GetBytes(h)).ToArray();
        var size = 1 + encoded.Sum(b => 1 + b.Length);
        var buf = new byte[size];
        var off = 0;
        buf[off++] = (byte)hops.Count;
        foreach (var bytes in encoded)
        {
            if (bytes.Length > byte.MaxValue)
            {
                throw new ArgumentException("callsign exceeds 255 bytes", nameof(hops));
            }
            buf[off++] = (byte)bytes.Length;
            bytes.CopyTo(buf.AsSpan(off));
            off += bytes.Length;
        }
        return buf;
    }

    private static IReadOnlyList<string> DecodeCallsignList(ReadOnlySpan<byte> buffer, ref int offset)
    {
        var count = buffer[offset++];
        var hops = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var len = buffer[offset++];
            hops.Add(Encoding.UTF8.GetString(buffer.Slice(offset, len)));
            offset += len;
        }
        return hops;
    }
}
