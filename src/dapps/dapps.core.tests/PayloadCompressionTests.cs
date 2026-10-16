using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using dapps.client.Compression;

namespace dapps.core.tests;

public sealed class PayloadCompressionTests
{
    // Real payloads from the G5ALF-3 / M0AHN-3 trace in #187, plus the
    // short-key form WPS replication sends now.
    private const string WpsPostLongKeys =
        """{"v":1,"origin":"DPSTST","seq":47,"epoch":1,"ts":1790410259080,"op":"post.insert","key":{"cid":1,"ts":1790410258990},"data":{"t":"cp","cid":1,"fc":"G5ALF","ts":1790410258990,"p":"1","dts":1790410259080}}""";
    private const string WpsPostShortKeys =
        """{"v":1,"o":"MB7NPW","s":66,"e":1,"ts":1790410266123,"a":"p.i","data":{"t":"cp","cid":1,"fc":"M0AHN","ts":1790410266050,"p":"Evening all, is anyone on the WPS channel tonight?","dts":1790410266123}}""";
    private const string WpsAck = """{"op":"ack","origin":"DPSTST","seq":48,"by":"M0AHN-3"}""";

    /// <summary>SHA-256 of every dictionary version that has shipped.</summary>
    private static readonly Dictionary<int, string> ShippedDictionaries = new()
    {
        [1] = "ee78c7f5ee12e76445c523a0c692ae6ec4e93741a221895c66b861653c6cf8d6",
    };

    /// <summary>
    /// A shipped dictionary must never change: peers and queued offers
    /// refer to it by version. If a hash here fails, don't update it -
    /// put the new bytes in a new version. A new version needs its hash
    /// added here before this passes.
    /// </summary>
    [Fact]
    public void EveryDictionaryVersion_IsPinned_AndUnchanged()
    {
        PayloadCompression.Versions.Should().BeEquivalentTo(ShippedDictionaries.Keys);
        foreach (var version in PayloadCompression.Versions)
        {
            Convert.ToHexStringLower(SHA256.HashData(PayloadCompression.DictionaryBytes(version)!))
                .Should().Be(ShippedDictionaries[version], $"dictionary v{version} has shipped and must not change");
        }
        PayloadCompression.Versions.Should().Contain(PayloadCompression.CurrentDictionaryVersion);
    }

    [Theory]
    [InlineData("z1", -1)]
    [InlineData("d", -1)]
    [InlineData("d", int.MaxValue)]
    public void AnOutOfRangeLen_IsRejectedNotAllocated(string format, int length)
    {
        var act = () => PayloadCompression.Decode(format, [1, 2, 3], length);
        act.Should().Throw<InvalidDataException>();
    }

    // ---- The 16 MB cap (#262): nothing allocated on the word of len ----

    private const long FewMegabytes = 4 * 1024 * 1024;

    private static long AllocatedBy(Action act)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        act();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Theory]
    [InlineData("p")]
    [InlineData("d")]
    [InlineData("z1")]
    public void ALenOverTheCap_IsRejected_WithoutAllocatingIt(string format)
    {
        var wire = new byte[] { 1, 2, 3, 4 };
        var allocated = AllocatedBy(() =>
            FluentActions.Invoking(() => PayloadCompression.Decode(format, wire, 2_000_000_000))
                .Should().Throw<InvalidDataException>().WithMessage("*out of range*"));
        allocated.Should().BeLessThan(FewMegabytes);
        FluentActions.Invoking(() => PayloadCompression.Decode(format, wire, PayloadCompression.MaxPayloadBytes + 1))
            .Should().Throw<InvalidDataException>().WithMessage("*out of range*");
    }

    [Fact]
    public void Deflate_ThatFallsShortOfAHugeLen_AllocatesWhatItInflatesTo_NotLen()
    {
        // Used to allocate len+1 up front: a few bytes claiming 16 MB cost 16 MB.
        var wire = ExchangeTestKit.Deflate("hello"u8.ToArray());
        var allocated = AllocatedBy(() =>
            FluentActions.Invoking(() => PayloadCompression.Decode("d", wire, PayloadCompression.MaxPayloadBytes))
                .Should().Throw<InvalidDataException>().WithMessage("*decoded to 5 bytes*"));
        allocated.Should().BeLessThan(FewMegabytes);
    }

    [Fact]
    public void Deflate_RightAtTheCap_StillDecodes()
    {
        var payload = new byte[PayloadCompression.MaxPayloadBytes];
        payload[^1] = 7;
        PayloadCompression.Decode("d", ExchangeTestKit.Deflate(payload), payload.Length).Should().Equal(payload);
    }

    [Theory]
    [InlineData(2_000_000_000UL)]
    [InlineData(16UL * 1024 * 1024)]
    public void Zstd_WhoseHeaderClaimsMoreThanLen_IsRejected_WithoutAllocatingIt(ulong claimed)
    {
        var payload = Encoding.UTF8.GetBytes(WpsPostShortKeys);
        var frame = PayloadCompression.TryCompress(payload)!.Value.Bytes;
        var lying = ExchangeTestKit.WithClaimedContentSize(frame, claimed);

        var allocated = AllocatedBy(() =>
            FluentActions.Invoking(() => PayloadCompression.Decode("z1", lying, payload.Length))
                .Should().Throw<InvalidDataException>());
        allocated.Should().BeLessThan(FewMegabytes);
    }

    [Fact]
    public void Zstd_WithTheHeaderRewrittenButTrue_StillDecodes()
    {
        // The test's own header rewrite, checked: an 8-byte content size
        // that tells the truth decodes as before.
        var payload = Encoding.UTF8.GetBytes(WpsPostShortKeys);
        var frame = PayloadCompression.TryCompress(payload)!.Value.Bytes;
        var rewritten = ExchangeTestKit.WithClaimedContentSize(frame, (ulong)payload.Length);
        rewritten.Should().NotEqual(frame);
        PayloadCompression.Decode("z1", rewritten, payload.Length).Should().Equal(payload);
    }

    [Theory]
    [InlineData(WpsPostLongKeys)]
    [InlineData(WpsPostShortKeys)]
    public void AWpsPost_CompressesWithTheCurrentDictionary_AndRoundTrips(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);

        var wire = PayloadCompression.TryCompress(payload);

        wire.Should().NotBeNull("a replication post is exactly what the dictionary was built for");
        wire!.Value.Format.Should().Be("z1");
        wire.Value.Bytes.Length.Should().BeLessThan(payload.Length / 2);
        PayloadCompression.Decode("z1", wire.Value.Bytes, payload.Length).Should().Equal(payload);
    }

    [Fact]
    public void AShortAck_GoesPlain()
    {
        // Too little to save to be worth an unreadable payload on a monitor.
        PayloadCompression.TryCompress(Encoding.UTF8.GetBytes(WpsAck)).Should().BeNull();
    }

    [Fact]
    public void RandomBytes_GoPlain()
    {
        var noise = new byte[300];
        new Random(42).NextBytes(noise);
        PayloadCompression.TryCompress(noise).Should().BeNull();
    }

    [Fact]
    public void Deflate_IsDecoded()
    {
        var payload = Encoding.UTF8.GetBytes(WpsPostLongKeys);
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(payload);

        PayloadCompression.Decode("d", ms.ToArray(), payload.Length).Should().Equal(payload);
    }

    [Fact]
    public void Deflate_ThatExpandsBeyondLen_IsRejectedNotInflated()
    {
        // A small compressed payload claiming to be tiny but expanding to
        // a megabyte: decoding stops at len+1 bytes and fails.
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(new byte[1_000_000]);

        var act = () => PayloadCompression.Decode("d", ms.ToArray(), 10);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Zstd_WithTheWrongLen_IsRejected()
    {
        var payload = Encoding.UTF8.GetBytes(WpsPostShortKeys);
        var wire = PayloadCompression.TryCompress(payload)!.Value;

        var act = () => PayloadCompression.Decode("z1", wire.Bytes, payload.Length - 1);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Garbage_UnderAZstdFormat_IsRejected()
    {
        var act = () => PayloadCompression.Decode("z1", "not zstd at all"u8.ToArray(), 50);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("d", true)]
    [InlineData("z1", true)]
    [InlineData("z2", false)]
    [InlineData("z0", false)]
    [InlineData("z", false)]
    [InlineData("zz", false)]
    [InlineData("q", false)]
    public void CanDecode_KnowsWhichFormatsThisBuildReads(string format, bool expected) =>
        PayloadCompression.CanDecode(format).Should().Be(expected);

    [Fact]
    public void AnUnknownDictionaryVersion_IsRejected()
    {
        var act = () => PayloadCompression.Decode("z9", [1, 2, 3], 3);
        act.Should().Throw<InvalidDataException>().WithMessage("*z9*");
    }
}
