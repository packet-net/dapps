using System.Text;
using AwesomeAssertions;
using dapps.client.Discovery;

namespace dapps.core.tests;

/// <summary>
/// Wire-form tests for the discovery beacon. Anyone running a packet
/// monitor on a frequency carrying DAPPS UI frames sees this exact
/// text - keeping it parseable both ways is important.
/// </summary>
public class BeaconCodecTests
{
    [Fact]
    public void Encode_KnownInput_MatchesWireForm()
    {
        var beacon = new BeaconFrame("M0LTE-9", Hops: 0, Ttl: 300, Bearer: new AgwBearerHint(0));
        var bytes = BeaconCodec.Encode(beacon);
        Encoding.ASCII.GetString(bytes).Should().Be("DAPPS v1 callsign=M0LTE-9 hops=0 ttl=300 end=1");
    }

    [Fact]
    public void Decode_RoundTripsAllFields()
    {
        var input = new BeaconFrame("G7XYZ-9", Hops: 2, Ttl: 1800, Bearer: new AgwBearerHint(0));
        var encoded = BeaconCodec.Encode(input);

        var hint = new UdpBearerHint("10.0.0.5:1881");
        BeaconCodec.TryParse(encoded, hint, out var decoded).Should().BeTrue();
        decoded!.Callsign.Should().Be("G7XYZ-9");
        decoded.Hops.Should().Be(2);
        decoded.Ttl.Should().Be(1800);
        decoded.Bearer.Should().Be(hint, "the bearer hint is supplied by the receiver, not carried on wire");
    }

    [Fact]
    public void Decode_MissingMagic_Fails()
    {
        var bytes = "NOT-DAPPS callsign=G0BAD hops=0 ttl=60"u8.ToArray();
        BeaconCodec.TryParse(bytes, new AgwBearerHint(0), out var b).Should().BeFalse();
        b.Should().BeNull();
    }

    [Fact]
    public void Decode_TrailingNewline_Tolerated()
    {
        var bytes = "DAPPS v1 callsign=A hops=0 ttl=60\r\n"u8.ToArray();
        BeaconCodec.TryParse(bytes, new AgwBearerHint(0), out var b).Should().BeTrue();
        b!.Callsign.Should().Be("A");
    }

    [Fact]
    public void Decode_UnknownKey_IgnoredForwardCompat()
    {
        var bytes = "DAPPS v1 callsign=G7 hops=1 ttl=60 future_field=hello"u8.ToArray();
        BeaconCodec.TryParse(bytes, new AgwBearerHint(0), out var b).Should().BeTrue();
        b!.Callsign.Should().Be("G7");
        b.Hops.Should().Be(1);
        b.Ttl.Should().Be(60);
    }

    [Theory]
    [InlineData("DAPPS v1 hops=0 ttl=60")]                // missing callsign
    [InlineData("DAPPS v1 callsign= hops=0 ttl=60")]      // empty callsign
    [InlineData("DAPPS v1 callsign=A hops=-1 ttl=60")]    // negative hops
    [InlineData("DAPPS v1 callsign=A hops=0 ttl=0")]      // ttl must be positive
    [InlineData("DAPPS v1 callsign=A hops=0 ttl=foo")]    // ttl not int
    public void Decode_Malformed_Fails(string raw)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        BeaconCodec.TryParse(bytes, new AgwBearerHint(0), out var b).Should().BeFalse();
    }

    // 64-bit LinBPQ's AGW monitor hands a UI frame over with 4 stale
    // bytes after its data (M0LTE/linbpq#75): printable, digits, spaces.
    [Theory]
    [InlineData("@`\u0001x")]
    [InlineData("W~12")]
    [InlineData("9 Z3")]
    [InlineData("1234")]
    public void Decode_JunkAfterTheEndField_IsIgnored(string junk)
    {
        var bytes = Encoding.ASCII.GetBytes($"DAPPS v1 callsign=M0LTE-9 hops=1 ttl=600 end=1{junk}\r");
        BeaconCodec.TryParse(bytes, new AgwBearerHint(0), out var b).Should().BeTrue();
        (b!.Callsign, b.Hops, b.Ttl).Should().Be(("M0LTE-9", 1, 600));
    }

    [Theory]
    [InlineData("@`\u0001x")]
    [InlineData("W~")]
    [InlineData("#9")]
    public void Decode_FromAnOlderSender_JunkOnTheLastField_IsCutOff(string junk)
    {
        // No end field: the junk lands on ttl, which takes its leading digits.
        var bytes = Encoding.ASCII.GetBytes($"DAPPS v1 callsign=M0LTE-9 hops=1 ttl=600{junk}\r");
        BeaconCodec.TryParse(bytes, new AgwBearerHint(0), out var b).Should().BeTrue();
        (b!.Callsign, b.Hops, b.Ttl).Should().Be(("M0LTE-9", 1, 600));
    }

    [Theory]
    [InlineData(" end")]             // seen from a 0.52 sender through 64-bit LinBPQ
    [InlineData(" end junk")]
    [InlineData(" W~ #9")]
    [InlineData("@` x")]             // on the last field and after it
    public void Decode_FromAnOlderSender_JunkWordsAfterTheLastField_AreIgnored(string junk)
    {
        var bytes = Encoding.ASCII.GetBytes($"DAPPS v1 callsign=M0LTE-9 hops=1 ttl=600{junk}\r");
        BeaconCodec.TryParse(bytes, new AgwBearerHint(0), out var b).Should().BeTrue();
        (b!.Callsign, b.Hops, b.Ttl).Should().Be(("M0LTE-9", 1, 600));
    }

    [Theory]
    [InlineData("DAPPS v1 callsign=A junk hops=0 ttl=60")]        // a word between fields
    [InlineData("DAPPS v1 callsign=A hops=0 ttl=60 end x=1")]      // a field after the junk
    [InlineData("DAPPS v1 end")]                                  // no fields at all
    public void Decode_JunkWordsAnywhereButTheEnd_Fail(string raw)
    {
        BeaconCodec.TryParse(Encoding.ASCII.GetBytes(raw), new AgwBearerHint(0), out _).Should().BeFalse();
    }

    [Fact]
    public void Decode_FromAnOlderSender_DigitJunk_IsCappedAtADay()
    {
        var bytes = Encoding.ASCII.GetBytes("DAPPS v1 callsign=M0LTE-9 hops=1 ttl=5400123\r");
        BeaconCodec.TryParse(bytes, new AgwBearerHint(0), out var b).Should().BeTrue();
        b!.Ttl.Should().Be(BeaconCodec.MaxTolerantTtlSeconds);
    }

    [Theory]
    [InlineData("DAPPS v1 callsign=A hops=0x ttl=60 end=1")]      // junk on a field that isn't last
    [InlineData("DAPPS v1 callsign=A hops=0 ttl=60x end=1")]      // nor on the last one when end= follows it
    [InlineData("DAPPS v1 callsign=A hops=0 ttl=x60")]            // no leading digits
    [InlineData("DAPPS v1 callsign=A hops=0 ttl=0@`")]            // still positive
    public void Decode_JunkAnywhereElse_Fails(string raw)
    {
        BeaconCodec.TryParse(Encoding.ASCII.GetBytes(raw), new AgwBearerHint(0), out _).Should().BeFalse();
    }
}
