using System.Buffers.Binary;
using System.Text;

namespace dapps.client.Backhaul.Datagram;

/// <summary>
/// Splits an opaque byte buffer into fragments of size ≤ MTU and
/// reassembles them on the receiving side. Bearer-agnostic - the UDP
/// backhaul uses it today; a MeshCore Companion / KISS adapter with a
/// similarly small MTU will reuse it directly. Plan A0.3: fragmentation
/// is DAPPS-owned, not bearer-owned.
///
/// Fragment format:
/// <code>
///   [7]  message id (UTF-8 ASCII; matches DappsMessage 7-hex id)
///   [2]  seq          (uint16 LE - 0-based fragment index)
///   [2]  count        (uint16 LE - total fragments for this message)
///   [2]  chunk len    (uint16 LE)
///   [N]  chunk
/// </code>
/// 13-byte header. With MTU=200 a fragment carries up to 187 chunk
/// bytes; tests can dial MTU lower (e.g. 64) to force fragmentation
/// of short messages.
/// </summary>
public static class Packetiser
{
    public const int IdLength = 7;
    public const int HeaderLength = IdLength + 2 + 2 + 2;

    /// <summary>Smallest MTU the packetiser tolerates: header + at least
    /// one chunk byte. Anything below this would mean a fragment with
    /// no payload, which would never reassemble.</summary>
    public const int MinMtu = HeaderLength + 1;

    /// <summary>
    /// Split <paramref name="buffer"/> into fragments tagged with
    /// <paramref name="messageId"/>. The returned datagrams are each
    /// ≤ <paramref name="mtu"/> bytes including the fragment header.
    /// </summary>
    public static IReadOnlyList<byte[]> Split(string messageId, byte[] buffer, int mtu)
    {
        if (messageId.Length != IdLength)
        {
            throw new ArgumentException($"messageId must be exactly {IdLength} chars", nameof(messageId));
        }
        if (mtu < MinMtu)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu),
                $"mtu must be at least {MinMtu} (header + 1 chunk byte)");
        }

        var maxChunk = mtu - HeaderLength;
        var idBytes = Encoding.ASCII.GetBytes(messageId);

        // Empty buffers still produce a single fragment with chunk len 0
        // - the receiver needs to know the message exists at all to
        // deliver it via the inbox.
        var fragmentCount = buffer.Length == 0
            ? 1
            : (buffer.Length + maxChunk - 1) / maxChunk;
        if (fragmentCount > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"buffer fragments to {fragmentCount} pieces at mtu={mtu}; exceeds 65535 cap");
        }

        var output = new List<byte[]>(fragmentCount);
        for (var seq = 0; seq < fragmentCount; seq++)
        {
            var offset = seq * maxChunk;
            var chunkLen = Math.Min(maxChunk, buffer.Length - offset);
            if (chunkLen < 0) chunkLen = 0;

            var fragment = new byte[HeaderLength + chunkLen];
            idBytes.CopyTo(fragment.AsSpan(0));
            BinaryPrimitives.WriteUInt16LittleEndian(fragment.AsSpan(IdLength, 2), (ushort)seq);
            BinaryPrimitives.WriteUInt16LittleEndian(fragment.AsSpan(IdLength + 2, 2), (ushort)fragmentCount);
            BinaryPrimitives.WriteUInt16LittleEndian(fragment.AsSpan(IdLength + 4, 2), (ushort)chunkLen);
            if (chunkLen > 0)
            {
                buffer.AsSpan(offset, chunkLen).CopyTo(fragment.AsSpan(HeaderLength));
            }
            output.Add(fragment);
        }
        return output;
    }

    /// <summary>
    /// Parse a single fragment without copying the chunk. <see cref="Reassembler"/>
    /// owns the lifetime of the chunk bytes.
    /// </summary>
    public static FragmentHeader ParseHeader(ReadOnlySpan<byte> fragment)
    {
        if (fragment.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"fragment is shorter than {HeaderLength}-byte header (got {fragment.Length})");
        }
        var id = Encoding.ASCII.GetString(fragment[..IdLength]);
        var seq = BinaryPrimitives.ReadUInt16LittleEndian(fragment.Slice(IdLength, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(fragment.Slice(IdLength + 2, 2));
        var chunkLen = BinaryPrimitives.ReadUInt16LittleEndian(fragment.Slice(IdLength + 4, 2));
        if (HeaderLength + chunkLen > fragment.Length)
        {
            throw new InvalidDataException(
                $"fragment claims chunk length {chunkLen} but only {fragment.Length - HeaderLength} chunk bytes available");
        }
        return new FragmentHeader(id, seq, count, chunkLen);
    }
}

public readonly record struct FragmentHeader(string Id, ushort Seq, ushort Count, ushort ChunkLength);

/// <summary>
/// Collects fragments and reports when a message is complete. Not
/// thread-safe; the UDP listener holds one of these per receive loop
/// and processes fragments serially, so external locking isn't
/// required.
///
/// Stale-reassembly cleanup is the listener's job: call
/// <see cref="DropOlderThan"/> periodically with a deadline so a
/// message that lost a fragment doesn't pin memory forever.
/// </summary>
public sealed class Reassembler
{
    private sealed class Pending
    {
        public ushort Count;
        public byte[]?[] Chunks = [];
        public int Received;
        public DateTime FirstSeen;
    }

    private readonly Dictionary<string, Pending> _byId = new(StringComparer.Ordinal);

    /// <summary>
    /// Accept a fragment. If this completes a message, returns the
    /// reassembled buffer; otherwise returns null and the fragment is
    /// retained until the rest arrive.
    /// </summary>
    public byte[]? Accept(byte[] fragment, DateTime now)
    {
        var header = Packetiser.ParseHeader(fragment);
        if (header.Count == 0) return null;
        if (header.Seq >= header.Count) return null;

        if (!_byId.TryGetValue(header.Id, out var pending))
        {
            pending = new Pending
            {
                Count = header.Count,
                Chunks = new byte[]?[header.Count],
                FirstSeen = now,
            };
            _byId[header.Id] = pending;
        }
        else if (pending.Count != header.Count)
        {
            // Conflicting fragment count for the same id - likely a sender
            // restart with the same id mid-stream. Drop the old state.
            pending = new Pending
            {
                Count = header.Count,
                Chunks = new byte[]?[header.Count],
                FirstSeen = now,
            };
            _byId[header.Id] = pending;
        }

        if (pending.Chunks[header.Seq] != null) return null; // duplicate

        var chunk = new byte[header.ChunkLength];
        if (header.ChunkLength > 0)
        {
            fragment.AsSpan(Packetiser.HeaderLength, header.ChunkLength).CopyTo(chunk);
        }
        pending.Chunks[header.Seq] = chunk;
        pending.Received++;

        if (pending.Received < pending.Count) return null;

        // Complete - concatenate.
        _byId.Remove(header.Id);
        var totalLen = pending.Chunks.Sum(c => c?.Length ?? 0);
        var assembled = new byte[totalLen];
        var off = 0;
        foreach (var c in pending.Chunks)
        {
            if (c is null || c.Length == 0) continue;
            c.CopyTo(assembled.AsSpan(off));
            off += c.Length;
        }
        return assembled;
    }

    /// <summary>Drop reassembly state for any message whose first
    /// fragment is older than <paramref name="cutoff"/>. Returns the
    /// number of incomplete reassemblies discarded.</summary>
    public int DropOlderThan(DateTime cutoff)
    {
        var stale = _byId.Where(kv => kv.Value.FirstSeen < cutoff).Select(kv => kv.Key).ToList();
        foreach (var id in stale) _byId.Remove(id);
        return stale.Count;
    }
}
