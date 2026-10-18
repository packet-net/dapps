using System.Text;
using AwesomeAssertions;
using dapps.client.Discovery;

namespace dapps.core.tests;

/// <summary>
/// Plan B6.2 - wire-shape tests for the solicit codec. Mirror of the
/// beacon-codec tests; covers the round-trip plus the rejection cases
/// the parser has to get right (so a stray beacon or a near-miss
/// payload doesn't decode to a SolicitFrame).
/// </summary>
public sealed class SolicitCodecTests
{
    [Fact]
    public void Encode_HappyPath_ProducesAsciiLine()
    {
        var bytes = SolicitCodec.Encode(new SolicitFrame("M0LTE-9"));
        Encoding.ASCII.GetString(bytes).Should().Be("DAPPS v1 solicit callsign=M0LTE-9 end=1");
    }

    [Fact]
    public void TryParse_RoundTrip_RecoversCallsign()
    {
        var bytes = SolicitCodec.Encode(new SolicitFrame("G0AAA-7"));
        SolicitCodec.TryParse(bytes, out var solicit).Should().BeTrue();
        solicit.Should().NotBeNull();
        solicit!.Callsign.Should().Be("G0AAA-7");
    }

    [Fact]
    public void TryParse_TolerantTrailingNewlines_StillParses()
    {
        var bytes = Encoding.ASCII.GetBytes("DAPPS v1 solicit callsign=M0LTE-9\r\n");
        SolicitCodec.TryParse(bytes, out var solicit).Should().BeTrue();
        solicit!.Callsign.Should().Be("M0LTE-9");
    }

    [Fact]
    public void TryParse_BeaconPayload_DoesNotMatch()
    {
        // Critical: a beacon's wire form ("DAPPS v1 callsign=…") must
        // not be accepted as a solicit. The bearers' read loops try the
        // solicit codec first, so a false positive here would silently
        // swallow every beacon.
        var beacon = Encoding.ASCII.GetBytes("DAPPS v1 callsign=M0LTE-9 hops=0 ttl=600");
        SolicitCodec.TryParse(beacon, out var solicit).Should().BeFalse();
        solicit.Should().BeNull();
    }

    [Fact]
    public void TryParse_UnrelatedPrefix_DoesNotMatch()
    {
        var bytes = Encoding.ASCII.GetBytes("hello world");
        SolicitCodec.TryParse(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_MissingCallsign_DoesNotMatch()
    {
        var bytes = Encoding.ASCII.GetBytes("DAPPS v1 solicit");
        SolicitCodec.TryParse(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_EmptyCallsignValue_DoesNotMatch()
    {
        var bytes = Encoding.ASCII.GetBytes("DAPPS v1 solicit callsign=");
        SolicitCodec.TryParse(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_UnknownExtraKv_StillMatches()
    {
        // Forward-compat: future fields shouldn't break old parsers.
        // Same convention as the beacon codec.
        var bytes = Encoding.ASCII.GetBytes("DAPPS v1 solicit callsign=M0LTE-9 future=value");
        SolicitCodec.TryParse(bytes, out var solicit).Should().BeTrue();
        solicit!.Callsign.Should().Be("M0LTE-9");
    }

    [Theory]
    [InlineData("@`\u0001x")]
    [InlineData("9 Z3")]
    [InlineData("12AB")]
    public void TryParse_JunkAfterTheEndField_IsIgnored(string junk)
    {
        var bytes = Encoding.ASCII.GetBytes($"DAPPS v1 solicit callsign=M0LTE-9 end=1{junk}\r");
        SolicitCodec.TryParse(bytes, out var solicit).Should().BeTrue();
        solicit!.Callsign.Should().Be("M0LTE-9");
    }

    [Theory]
    [InlineData("@`\u0001x")]
    [InlineData("~#")]
    [InlineData(" ")]
    public void TryParse_FromAnOlderSender_JunkAfterTheCallsign_IsCutOff(string junk)
    {
        var bytes = Encoding.ASCII.GetBytes($"DAPPS v1 solicit callsign=M0LTE-9{junk}\r");
        SolicitCodec.TryParse(bytes, out var solicit).Should().BeTrue();
        solicit!.Callsign.Should().Be("M0LTE-9");
    }

    [Theory]
    [InlineData("DAPPS v1 solicit callsign=M0LTE-912")]   // digit junk: not a callsign any more
    [InlineData("DAPPS v1 solicit callsign=M0LTE-9AB")]   // letter junk: likewise
    [InlineData("DAPPS v1 solicit callsign=@`")]          // nothing callsign-like at all
    public void TryParse_FromAnOlderSender_JunkThatLeavesNoCallsign_Fails(string raw)
    {
        SolicitCodec.TryParse(Encoding.ASCII.GetBytes(raw), out _).Should().BeFalse();
    }
}
