using System.Buffers.Binary;
using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
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

    [Fact]
    public void ChannelMonitor_OccupancyRisesWithTrafficThenPrunes()
    {
        var region = Regions.Find("uk-test")!;
        var m = new ChannelMonitor(region, TimeSpan.FromSeconds(10));
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        m.OccupancyFraction(t0).Should().Be(0);
        m.SinceLastHeard(t0).Should().Be(TimeSpan.MaxValue);

        for (var i = 0; i < 5; i++) m.RecordHeard(150, t0.AddMilliseconds(i * 10));
        m.HeardCount.Should().Be(5);
        m.OccupancyFraction(t0.AddSeconds(1)).Should().BeGreaterThan(0);
        m.SinceLastHeard(t0.AddSeconds(1)).Should().BeLessThan(TimeSpan.FromSeconds(2));

        // Once the heard packets age past the window, occupancy returns to zero.
        m.OccupancyFraction(t0.AddSeconds(30)).Should().Be(0);
    }

    [Fact]
    public void TxBudget_Refund_ReturnsTheReservation()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var b = new TxBudget(secondsPerHour: 1.0); // 1000 ms/hr

        b.TryReserve(700, now, out _, out var t1).Should().BeTrue();
        b.TryReserve(700, now, out _, out _).Should().BeFalse("1400ms > 1000ms budget");
        b.Refund(t1);
        b.UsedSeconds(now).Should().BeApproximately(0, 0.001);
        b.TryReserve(900, now, out _, out _).Should().BeTrue("budget was refunded");
    }

    [Fact]
    public void TxBudget_Refund_RemovesTheSpecificReservation_NotJustTheLast()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var b = new TxBudget(secondsPerHour: 1.0);

        b.TryReserve(300, now, out _, out var a).Should().BeTrue();
        b.TryReserve(300, now, out _, out _).Should().BeTrue();
        b.Refund(a);   // refund the FIRST reservation, not the most recent
        b.UsedSeconds(now).Should().BeApproximately(0.3, 0.001, "only the second 300ms reservation remains");
    }

    [Fact]
    public void ChannelData_ParseRecv_ShortFrame_ThrowsInvalidData()
    {
        var act = () => ChannelData.ParseRecv([0x1B, 0, 0]);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Reliability_TrackThenAck_ConfirmsAndClears()
    {
        var r = new MeshCoreReliability();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var m = SampleMessage("hi") with { Id = "abc0001", Originator = "GB7A-1", Destination = "GB7B-1" };

        r.Track(m, "GB7A-1", now);
        r.PendingCount.Should().Be(1);
        r.OnAck("abc0001").Should().BeTrue();
        r.PendingCount.Should().Be(0);
        r.Confirmed.Should().Be(1);
        r.OnAck("missing").Should().BeFalse();
    }

    [Fact]
    public void Reliability_DueResends_RespectBackoffAndDeadline()
    {
        var r = new MeshCoreReliability(new MeshCoreReliability.Options(
            BaseBackoff: TimeSpan.FromSeconds(20), Multiplier: 2.0,
            MaxBackoff: TimeSpan.FromSeconds(120), MaxLifetime: TimeSpan.FromSeconds(60)));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var m = SampleMessage("hi") with { Id = "abc0002", Ttl = 60, Originator = "GB7A-1" };

        r.Track(m, "GB7A-1", now);
        r.DueResends(now).Should().BeEmpty("first backoff is 20s away");
        r.DueResends(now.AddSeconds(21)).Should().ContainSingle();
        r.DueResends(now.AddSeconds(21)).Should().ContainSingle("DueResends must not advance backoff on its own");
        r.MarkResent("abc0002", now.AddSeconds(21));
        r.DueResends(now.AddSeconds(22)).Should().BeEmpty("backoff advanced after MarkResent");
        r.DueResends(now.AddSeconds(120)).Should().BeEmpty("past the lifetime deadline");
        r.DropExpired(now.AddSeconds(120)).Should().ContainSingle();
        r.Expired.Should().Be(1);
    }

    [Fact]
    public void Reliability_BuildAck_IsAckAddressedToOriginator()
    {
        var m = SampleMessage("data") with { Id = "data001", Originator = "GB7A-1", Destination = "GB7B-1" };
        var ack = MeshCoreReliability.BuildAck(m, localCallsign: "GB7B-1", ackId: "ack0001");

        MeshCoreReliability.IsAck(ack).Should().BeTrue();
        MeshCoreReliability.AckedId(ack).Should().Be("data001");
        ack.Destination.Should().Be("GB7A-1");
        ack.Originator.Should().Be("GB7B-1");
        MeshCoreReliability.IsAck(m).Should().BeFalse();
    }

    [Fact]
    public void RouteBuilder_CopiesMeshCoreChannelHint()
    {
        var route = RouteBuilder.FromNeighbour(
            new DbNeighbour { Callsign = "GB7XYZ-1", MeshCoreChannel = "dapps" }, defaultBearerPort: null);
        route.Callsign.Should().Be("GB7XYZ-1");
        route.MeshCoreChannel.Should().Be("dapps");
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
