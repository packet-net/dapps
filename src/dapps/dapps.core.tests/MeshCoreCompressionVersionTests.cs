using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.meshcore;
using Xunit;

namespace dapps.core.tests;

/// <summary>
/// Versioned shared dictionary (#23): each compressed MeshCore frame stamps the
/// dictionary version it was produced with, so a receiver decompresses with the
/// matching dictionary and safely DROPS a frame from a dictionary version it doesn't
/// hold (instead of feeding zstd a mismatched dictionary and delivering corruption).
/// </summary>
public sealed class MeshCoreCompressionVersionTests
{
    private static BackhaulMessage Sample(string payload) => new(
        Id: "ab12cd3", Destination: "GB7ABC-1", Salt: 42, Ttl: 1800,
        Payload: Encoding.UTF8.GetBytes(payload),
        Originator: "M0LTE-7", LinkSourceCallsign: "M0LTE-7");

    // A short payload compresses to a single fragment, so there's exactly one frame to
    // inspect / mutate.
    private const string ShortPayload = "GM all de M0LTE, 73";

    [Fact]
    public void CompressedFrame_StampsCurrentDictionaryVersion()
    {
        var frames = new MeshCoreChannelTransport().ToFrames(Sample(ShortPayload), DappsCompression.Mode.ZstdDict);

        frames.Should().ContainSingle("a short payload is one fragment");
        var frame = frames[0];
        (frame[0] & 1).Should().Be(1, "the compressed flag must be set");
        frame[1].Should().Be(DappsCompression.CurrentDictionaryVersion, "byte1 carries the dictionary version");
    }

    [Fact]
    public void UncompressedFrame_HasNoVersionByte()
    {
        // Uncompressed frames stay byte-identical to the pre-versioning format: no
        // version byte, fragment begins at offset 1.
        var frames = new MeshCoreChannelTransport().ToFrames(Sample(ShortPayload), DappsCompression.Mode.None);

        var frame = frames[0];
        (frame[0] & 1).Should().Be(0, "the compressed flag must be clear");

        var rx = new MeshCoreChannelTransport();
        BackhaulMessage? got = null;
        foreach (var f in frames)
        {
            var r = rx.Ingest(f, DateTime.UtcNow);
            if (r.Kind == MeshCoreChannelTransport.Kind.BackhaulComplete) got = r.Message;
        }
        got.Should().NotBeNull();
        Encoding.UTF8.GetString(got!.Payload).Should().Be(ShortPayload);
    }

    [Fact]
    public void CurrentVersion_RoundTrips()
    {
        var original = Sample(ShortPayload);
        var frames = new MeshCoreChannelTransport().ToFrames(original, DappsCompression.Mode.ZstdDict);

        var rx = new MeshCoreChannelTransport();
        BackhaulMessage? got = null;
        foreach (var f in frames)
        {
            var r = rx.Ingest(f, DateTime.UtcNow);
            if (r.Kind == MeshCoreChannelTransport.Kind.BackhaulComplete) got = r.Message;
        }
        got.Should().NotBeNull();
        Encoding.UTF8.GetString(got!.Payload).Should().Be(ShortPayload);
    }

    [Fact]
    public void UnknownDictionaryVersion_IsDroppedAsUnsupported_NotDelivered()
    {
        var frames = new MeshCoreChannelTransport().ToFrames(Sample(ShortPayload), DappsCompression.Mode.ZstdDict);
        frames.Should().ContainSingle();

        // Simulate a peer on a future dictionary: same compressed body, unknown version.
        const byte futureVersion = 99;
        DappsCompression.IsKnownVersion(futureVersion).Should().BeFalse("test premise");
        frames[0][1] = futureVersion;

        var rx = new MeshCoreChannelTransport();
        var results = frames.Select(f => rx.Ingest(f, DateTime.UtcNow)).ToList();

        results.Should().Contain(r => r.Kind == MeshCoreChannelTransport.Kind.Unsupported,
            "a frame from an unknown dictionary version must be flagged Unsupported");
        results.Should().NotContain(r => r.Kind == MeshCoreChannelTransport.Kind.BackhaulComplete,
            "it must never be decoded/delivered with the wrong dictionary");
    }

    [Fact]
    public void Decompress_KnownVersion_Recovers_UnknownVersion_Throws()
    {
        var payload = Encoding.UTF8.GetBytes("the quick brown fox de M0LTE 73");
        var compressed = DappsCompression.Compress(DappsCompression.Mode.ZstdDict, payload);

        DappsCompression.Decompress(DappsCompression.CurrentDictionaryVersion, compressed)
            .Should().Equal(payload);

        var act = () => DappsCompression.Decompress(200, compressed);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void IsKnownVersion_TrueForCurrent_FalseForOthers()
    {
        DappsCompression.IsKnownVersion(DappsCompression.CurrentDictionaryVersion).Should().BeTrue();
        DappsCompression.IsKnownVersion(0).Should().BeFalse();
        DappsCompression.IsKnownVersion(255).Should().BeFalse();
    }
}
