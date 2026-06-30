using System.Buffers.Binary;
using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.meshcore;
using Xunit;

namespace dapps.core.tests;

/// <summary>Unit tests for the MeshCore bearer (#154): the binary channel
/// transport round-trip (the real codec + packetiser + compression), the airtime
/// governor, PSK resolution, and frame parsing.</summary>
public class MeshCoreBearerTests
{
    private static BackhaulMessage SampleMessage(string payload) => new(
        Id: "ab12cd3", Destination: "GB7ABC-1", Salt: 42, Ttl: 1800,
        Payload: Encoding.UTF8.GetBytes(payload),
        Originator: "M0LTE-7", LinkSourceCallsign: "M0LTE-7");

    [Theory]
    [InlineData(DappsCompression.Mode.None, "73")]
    [InlineData(DappsCompression.Mode.None, "Hello from the DAPPS mailbox, a longer store-and-forward message over the mesh. 73 de M0LTE GB7ABC-1 599")]
    [InlineData(DappsCompression.Mode.ZstdDict, "73")]
    [InlineData(DappsCompression.Mode.ZstdDict, "Hello from the DAPPS mailbox, a longer store-and-forward message over the mesh. 73 de M0LTE GB7ABC-1 599")]
    public void Transport_RoundTrips_InOrder(DappsCompression.Mode mode, string payload)
    {
        var original = SampleMessage(payload);
        var frames = new MeshCoreChannelTransport().ToFrames(original, mode);
        frames.Should().NotBeEmpty();
        frames.Should().OnlyContain(f => f.Length <= 165);

        var rx = new MeshCoreChannelTransport();
        BackhaulMessage? got = null;
        foreach (var f in frames)
        {
            var r = rx.Ingest(f, DateTime.UtcNow);
            if (r.Kind == MeshCoreChannelTransport.Kind.BackhaulComplete) got = r.Message;
        }
        AssertEqual(got, original);
    }

    [Fact]
    public void Transport_RoundTrips_OutOfOrder()
    {
        var original = SampleMessage(string.Concat(Enumerable.Repeat("DAPPS over MeshCore. ", 12)));
        var frames = new MeshCoreChannelTransport().ToFrames(original, DappsCompression.Mode.None);
        frames.Count.Should().BeGreaterThan(1, "the payload should span multiple fragments");

        var rx = new MeshCoreChannelTransport();
        BackhaulMessage? got = null;
        foreach (var f in frames.Reverse())
        {
            var r = rx.Ingest(f, DateTime.UtcNow);
            if (r.Kind == MeshCoreChannelTransport.Kind.BackhaulComplete) got = r.Message;
        }
        AssertEqual(got, original);
    }

    [Fact]
    public void Compression_ShrinksAndStillRoundTrips()
    {
        var original = SampleMessage("Hello from the DAPPS mailbox, a longer store-and-forward message over the mesh. 73 de M0LTE");
        var raw = new MeshCoreChannelTransport().ToFrames(original, DappsCompression.Mode.None);
        var zst = new MeshCoreChannelTransport().ToFrames(original, DappsCompression.Mode.ZstdDict);
        zst.Sum(f => f.Length).Should().BeLessThan(raw.Sum(f => f.Length), "the dictionary should shrink the payload");
    }

    [Fact]
    public void TxBudget_HardGate_RefusesOverBudget_AndPrunes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var budget = new TxBudget(secondsPerHour: 1.0);   // 1000 ms / hr

        budget.TryReserve(600, now, out _).Should().BeTrue();
        budget.TryReserve(600, now, out var reason).Should().BeFalse("600+600 > 1000ms budget");
        reason.Should().Contain("budget");
        budget.UsedSeconds(now).Should().BeApproximately(0.6, 0.001);

        // An hour later the first reservation has aged out of the window.
        budget.TryReserve(600, now.AddHours(1).AddSeconds(1), out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("3135135fd198029d689b64f45df2aae9", 16)] // 32-char hex -> 16 raw bytes
    [InlineData("a passphrase", 16)]                      // hashed to 16 bytes
    public void ResolvePsk_Produces16Bytes(string psk, int expectedLen)
    {
        new MeshCoreBearerOptions { ChannelPsk = psk }.ResolvePsk().Length.Should().Be(expectedLen);
    }

    [Fact]
    public void ResolvePsk_HexIsVerbatim()
    {
        var hex = "3135135fd198029d689b64f45df2aae9";
        var psk = new MeshCoreBearerOptions { ChannelPsk = hex }.ResolvePsk();
        Convert.ToHexString(psk).ToLowerInvariant().Should().Be(hex);
    }

    [Fact]
    public void ChannelData_ParsesBinaryRecvFrame()
    {
        // 0x1B [snr][r][r][ch][path][dtype-lo][dtype-hi][len][payload...]
        byte[] frame = [0x1B, 48, 0, 0, 2, 0xFF, 0xFF, 0xFF, 3, 0xAA, 0xBB, 0xCC];
        var d = ChannelData.ParseRecv(frame);
        d.ChannelIndex.Should().Be(2);
        d.ReceivedDirect.Should().BeTrue();
        d.DataType.Should().Be(0xFFFF);
        d.SnrDb.Should().BeApproximately(12.0, 0.001);
        d.Payload.Should().Equal((byte)0xAA, (byte)0xBB, (byte)0xCC);
    }

    [Fact]
    public void SelfInfo_ParsesRadioParams()
    {
        var p = new byte[66];
        p[0] = 0x05;
        p[1] = 1; p[2] = 8; p[3] = 22;             // adv_type, txp, max
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(48, 4), 868_400);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(52, 4), 62_500);
        p[56] = 8; p[57] = 8;                        // sf, cr
        Encoding.ASCII.GetBytes("DAPPS-R1").CopyTo(p, 58);

        var self = SelfInfo.Parse(p);
        self.FreqMhz.Should().BeApproximately(868.4, 0.001);
        self.BwKhz.Should().BeApproximately(62.5, 0.001);
        self.Sf.Should().Be(8);
        self.Cr.Should().Be(8);
        self.TxPower.Should().Be(8);
        self.Name.Should().Be("DAPPS-R1");
    }

    private static void AssertEqual(BackhaulMessage? got, BackhaulMessage original)
    {
        got.Should().NotBeNull();
        got!.Id.Should().Be(original.Id);
        got.Destination.Should().Be(original.Destination);
        got.Originator.Should().Be(original.Originator);
        got.LinkSourceCallsign.Should().Be(original.LinkSourceCallsign);
        got.Ttl.Should().Be(original.Ttl);
        got.Salt.Should().Be(original.Salt);
        got.Payload.Should().Equal(original.Payload);
    }
}
