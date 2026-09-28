using System.Text;
using dapps.client.Backhaul;

namespace dapps.client;

/// <summary>
/// Writes the header line of a message on a DAPPSv1 session: <c>ihave</c>
/// (an offer) or <c>msg</c> (the payload follows straight after). The
/// receiving end parses it with <see cref="IHaveValidator"/>.
/// </summary>
public static class OfferLine
{
    /// <summary>The line for <paramref name="message"/> sent as
    /// <paramref name="format"/>, with <paramref name="compressedLength"/>
    /// set when that format is compressed. Ends in <c>\n</c>.</summary>
    public static string Build(string verb, BackhaulMessage message, string format, int? compressedLength) =>
        Build(verb, message.Id, message.Salt, format, compressedLength, message.Destination, message.Payload.Length,
            message.Ttl, message.Originator, message.MasterId, message.FragmentIndex, message.FragmentTotal,
            message.StreamId, message.StreamSeq, message.StreamGapTimeoutSeconds);

    public static string Build(
        string verb,
        string id,
        long? salt,
        string format,
        int? compressedLength,
        string destination,
        int length,
        int? ttl = null,
        string? originator = null,
        string? masterId = null,
        int? fragmentIndex = null,
        int? fragmentTotal = null,
        string? streamId = null,
        uint? streamSeq = null,
        uint? streamGapTimeoutSeconds = null)
    {
        // F2 multi-part: mid= and frag=N/M either both present or both
        // absent. Belt-and-braces - the receiver's parser also enforces
        // this - but catching it sender-side prevents a malformed line
        // from reaching the wire in the first place.
        var hasFragHeaders = !string.IsNullOrEmpty(masterId)
            && fragmentIndex.HasValue && fragmentTotal.HasValue;
        if (!hasFragHeaders
            && (!string.IsNullOrEmpty(masterId) || fragmentIndex.HasValue || fragmentTotal.HasValue))
        {
            throw new ArgumentException(
                "masterId, fragmentIndex, fragmentTotal must all be set together (multi-part) or all be null");
        }

        // Opt-in ordering keys. All three travel together; the receiver's
        // IHaveValidator rejects a partial set.
        var hasStream = !string.IsNullOrEmpty(streamId)
            && streamSeq.HasValue && streamGapTimeoutSeconds.HasValue;
        if (!hasStream
            && (!string.IsNullOrEmpty(streamId) || streamSeq.HasValue || streamGapTimeoutSeconds.HasValue))
        {
            throw new ArgumentException(
                "streamId, streamSeq, streamGapTimeoutSeconds must all be set together (opt-in ordering) or all be null");
        }

        var sb = new StringBuilder($"{verb} {id} len={length} fmt={format}");
        if (compressedLength.HasValue)
        {
            sb.Append($" clen={compressedLength.Value}");
        }
        sb.Append($" dst={destination}");
        if (salt.HasValue)
        {
            sb.Append($" s={salt}");
        }
        if (ttl.HasValue)
        {
            sb.Append($" ttl={ttl.Value}");
        }
        // F1 end-to-end source tracking. Emitted only when set - pre-F1
        // local submissions (or relayed messages with no upstream src=)
        // omit it so the receiver knows the originator is unknown.
        if (!string.IsNullOrEmpty(originator))
        {
            sb.Append($" src={originator}");
        }
        // F2 multi-part headers. The destination groups fragments by mid=.
        if (hasFragHeaders)
        {
            sb.Append($" mid={masterId} frag={fragmentIndex}/{fragmentTotal}");
        }
        if (hasStream)
        {
            sb.Append($" sid={streamId} sn={streamSeq} gt={streamGapTimeoutSeconds}");
        }
        sb.Append('\n');
        return sb.ToString();
    }
}
