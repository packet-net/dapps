using System.Globalization;
using System.Text;
using dapps.client;
using dapps.client.Compression;

namespace dapps.core.Services;

/// <summary>
/// Pure parsed-and-validated representation of an `ihave` line. Constructed
/// only by <see cref="IHaveValidator.Validate"/>; the receiver promises
/// every constraint the spec mandates is already checked here, so callers
/// can act on the fields without re-validating.
/// </summary>
public sealed record IHaveOffer(
    string Id,
    int Length,
    string Format,                          // "p", "d" or "z<N>" (zstd, shared dictionary N)
    long? Salt,
    int? CompressedLength,
    string Destination,
    int? Ttl,                               // residual lifetime in seconds, null = unset
    Dictionary<string, string> AdditionalHeaders,
    string? Originator = null,              // src= - originating callsign (F1), null when sender pre-dates F1
    string? MasterId = null,                // mid= - F2 multi-part: opaque grouping id, null = not fragmented
    FragmentInfo? Fragment = null,          // frag=N/M - F2 multi-part fragment index/total, null = not fragmented
    string? StreamId = null,                // sid= - opt-in ordering stream identifier (per sender)
    uint? StreamSeq = null,                 // sn=  - monotonic seq within (sender, sid)
    uint? StreamGapTimeoutSeconds = null);  // gt=  - 0 = strict, >0 = skip gap after N seconds

/// <summary>F2 multi-part fragment metadata. Index is 1-based; total is
/// the number of fragments in the original payload. Both must satisfy
/// <c>0 &lt; Index ≤ Total</c>; Total must be ≥ 2 (a single-fragment
/// "M=1" message is just a regular message, no fragmentation needed).</summary>
public sealed record FragmentInfo(int Index, int Total);

public sealed record OfferValidationResult
{
    public string? Id { get; init; }
    public IHaveOffer? Offer { get; init; }
    public string? Error { get; init; }

    public bool IsValid => Error is null && Offer is not null;

    public static OfferValidationResult Success(IHaveOffer offer)
        => new() { Id = offer.Id, Offer = offer };

    public static OfferValidationResult Fail(string? id, string error)
        => new() { Id = id, Error = error };
}

/// <summary>
/// Pure parser/validator for the `ihave` command line. Decoupled from I/O,
/// the database, and any framework concerns so the rejection paths can be
/// exercised by unit tests without standing up a TCP listener.
/// </summary>
public static class IHaveValidator
{
    private const string ChkSuffixPrefix = " chk=";
    private const int ChkValueLength = 4;

    private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
        { "len", "fmt", "s", "clen", "dst", "chk", "ttl", "src", "mid", "frag", "sid", "sn", "gt" };

    public static OfferValidationResult Validate(string ihaveCommand)
    {
        var parts = ihaveCommand.Split(' ');
        if (parts.Length < 2 || parts[0] != "ihave")
        {
            return OfferValidationResult.Fail(null, "not an ihave command");
        }

        var id = parts[1];
        if (string.IsNullOrEmpty(id))
        {
            return OfferValidationResult.Fail(null, "missing message id");
        }

        // Parse remaining tokens as KVs. Spec forbids `=` and space inside
        // either side, so a token's first `=` is always the separator.
        var kvps = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < parts.Length; i++)
        {
            var token = parts[i];
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1)
            {
                return OfferValidationResult.Fail(id, $"malformed key=value token: '{token}'");
            }
            kvps[token[..eq]] = token[(eq + 1)..];
        }

        // chk: when present, validates the rest of the line. Run this first
        // - if the line is corrupt, downstream field parsing is meaningless.
        if (kvps.TryGetValue("chk", out var chk))
        {
            var chkError = ValidateChecksum(ihaveCommand, chk);
            if (chkError != null) return OfferValidationResult.Fail(id, chkError);
        }

        if (!kvps.TryGetValue("len", out var lenStr))
            return OfferValidationResult.Fail(id, "len= is required");
        if (!int.TryParse(lenStr, NumberStyles.None, CultureInfo.InvariantCulture, out var len) || len < 0)
            return OfferValidationResult.Fail(id, "len= must be a non-negative integer");

        if (!kvps.TryGetValue("dst", out var dst))
            return OfferValidationResult.Fail(id, "dst= is required");

        // z<N> is zstd with shared dictionary N: an N this build doesn't
        // hold is refused like any unknown format, and the sender offers
        // the message again plain.
        var fmt = kvps.TryGetValue("fmt", out var fmtVal) ? fmtVal : "p";
        if (!PayloadCompression.CanDecode(fmt))
            return OfferValidationResult.Fail(id, $"unknown fmt={fmt}");

        var hasClen = kvps.TryGetValue("clen", out var clenStr);
        if (fmt != "p" && !hasClen)
            return OfferValidationResult.Fail(id, $"clen= is required when fmt={fmt}");
        if (fmt == "p" && hasClen)
            return OfferValidationResult.Fail(id, "clen= MUST NOT be supplied when fmt=p");
        int? clen = null;
        if (hasClen)
        {
            if (!int.TryParse(clenStr, NumberStyles.None, CultureInfo.InvariantCulture, out var clenValue) || clenValue < 0)
                return OfferValidationResult.Fail(id, "clen= must be a non-negative integer");
            clen = clenValue;
        }

        long? salt = null;
        if (kvps.TryGetValue("s", out var sStr))
        {
            if (!long.TryParse(sStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s))
                return OfferValidationResult.Fail(id, "s= must be a 64-bit signed integer");
            salt = s;
        }

        int? ttl = null;
        if (kvps.TryGetValue("ttl", out var ttlStr))
        {
            if (!int.TryParse(ttlStr, NumberStyles.None, CultureInfo.InvariantCulture, out var ttlValue) || ttlValue <= 0)
                return OfferValidationResult.Fail(id, "ttl= must be a positive integer (seconds)");
            ttl = ttlValue;
        }

        var headers = kvps
            .Where(kv => !ReservedKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // src=<callsign> - F1 end-to-end source tracking. Optional;
        // when absent the originator is unknown (could be the link
        // source on a single-hop send, or any upstream hop on a
        // pre-F1 relay path). Empty string is treated as absent.
        string? originator = null;
        if (kvps.TryGetValue("src", out var srcVal) && !string.IsNullOrEmpty(srcVal))
        {
            originator = srcVal;
        }

        // mid= and frag=N/M - F2 multi-part. Both present together or
        // both absent; mid= alone means "fragmented but how-many-of-
        // how-many is missing" (rejected); frag= alone means "fragment
        // metadata without an id to group on" (also rejected). Total
        // must be ≥ 2; a single-fragment message is just a normal
        // message, no fragmentation needed.
        string? masterId = null;
        FragmentInfo? fragment = null;
        var hasMid = kvps.TryGetValue("mid", out var midVal) && !string.IsNullOrEmpty(midVal);
        var hasFrag = kvps.TryGetValue("frag", out var fragVal) && !string.IsNullOrEmpty(fragVal);
        if (hasMid != hasFrag)
        {
            return OfferValidationResult.Fail(id,
                "mid= and frag= must both be present or both absent (multi-part requires both)");
        }
        if (hasMid && hasFrag)
        {
            masterId = midVal!;

            // frag wire form is "N/M"; both N and M positive integers,
            // N ≤ M, M ≥ 2.
            var slash = fragVal!.IndexOf('/');
            if (slash <= 0 || slash == fragVal.Length - 1)
            {
                return OfferValidationResult.Fail(id, $"frag= must be N/M; got '{fragVal}'");
            }
            if (!int.TryParse(fragVal.AsSpan(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out var fragN)
                || !int.TryParse(fragVal.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var fragM))
            {
                return OfferValidationResult.Fail(id, $"frag= N/M parts must be non-negative integers; got '{fragVal}'");
            }
            if (fragM < 2)
            {
                return OfferValidationResult.Fail(id,
                    "frag= total must be ≥ 2; single-part messages must omit mid/frag entirely");
            }
            if (fragN < 1 || fragN > fragM)
            {
                return OfferValidationResult.Fail(id,
                    $"frag= index must satisfy 1 ≤ N ≤ M; got {fragN}/{fragM}");
            }
            fragment = new FragmentInfo(fragN, fragM);
        }

        // sid= / sn= / gt= - opt-in ordering. All three travel together
        // or all three are absent; partial sets are an error since a
        // gappy implementation downstream couldn't reconstruct the
        // ordering contract. sn / gt are 32-bit unsigned: sn is the
        // monotonic seq, gt is the gap timeout in seconds (0 = strict).
        string? streamId = null;
        uint? streamSeq = null;
        uint? streamGapTimeout = null;
        var hasSid = kvps.TryGetValue("sid", out var sidVal) && !string.IsNullOrEmpty(sidVal);
        var hasSn = kvps.TryGetValue("sn", out var snVal) && !string.IsNullOrEmpty(snVal);
        var hasGt = kvps.TryGetValue("gt", out var gtVal) && !string.IsNullOrEmpty(gtVal);
        if (hasSid || hasSn || hasGt)
        {
            if (!(hasSid && hasSn && hasGt))
            {
                return OfferValidationResult.Fail(id,
                    "sid=, sn=, gt= must all be present together (opt-in ordering) or all be absent");
            }
            if (!uint.TryParse(snVal, NumberStyles.None, CultureInfo.InvariantCulture, out var snParsed))
            {
                return OfferValidationResult.Fail(id, "sn= must be a 32-bit unsigned integer");
            }
            if (!uint.TryParse(gtVal, NumberStyles.None, CultureInfo.InvariantCulture, out var gtParsed))
            {
                return OfferValidationResult.Fail(id, "gt= must be a 32-bit unsigned integer (0 = strict, >0 = seconds)");
            }
            streamId = sidVal!;
            streamSeq = snParsed;
            streamGapTimeout = gtParsed;
        }

        return OfferValidationResult.Success(new IHaveOffer(
            id, len, fmt, salt, clen, dst, ttl, headers, originator, masterId, fragment,
            streamId, streamSeq, streamGapTimeout));
    }

    /// <summary>
    /// Validate that `chk=NNNN` is the last KV, that no other `chk=` appears
    /// in the line, and that the CRC-16/CCITT-FALSE of the bytes-before-chk
    /// matches. Returns null on success, error string on any failure.
    /// </summary>
    private static string? ValidateChecksum(string ihaveCommand, string providedChk)
    {
        // Check the value's shape first; otherwise a wrong-length chk would
        // get caught by the position rule and surface a misleading error.
        if (providedChk.Length != ChkValueLength
            || !ushort.TryParse(providedChk, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var providedValue))
        {
            return "chk value is not 4 hex characters";
        }

        var prefixIndex = ihaveCommand.LastIndexOf(ChkSuffixPrefix, StringComparison.Ordinal);
        if (prefixIndex < 0)
        {
            return "chk parsed from KVs but not present in line as ' chk=' suffix";
        }

        if (prefixIndex + ChkSuffixPrefix.Length + ChkValueLength != ihaveCommand.Length)
        {
            return "chk MUST be the last KV on the line";
        }

        var firstChkIndex = ihaveCommand.IndexOf("chk=", StringComparison.Ordinal);
        if (firstChkIndex != prefixIndex + 1)
        {
            return "chk= must appear only as the last KV";
        }

        var coveredBytes = Encoding.UTF8.GetBytes(ihaveCommand[..prefixIndex]);
        var expected = Crc16CcittFalse.Compute(coveredBytes);

        return expected == providedValue ? null : "chk mismatch (line corruption?)";
    }
}
