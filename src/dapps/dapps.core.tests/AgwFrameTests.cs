using System.Buffers.Binary;
using System.Text;
using dapps.client.Transport.Agw;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace dapps.core.tests;

public class AgwFrameTests
{
    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(100, new[] { 100 })]
    [InlineData(256, new[] { 256 })]
    [InlineData(257, new[] { 256, 1 })]
    [InlineData(600, new[] { 256, 256, 88 })]
    public async Task WriteDataAsync_SplitsIntoFramesOfAtMost256Bytes(int length, int[] frameSizes)
    {
        // BPQ drops an AGW data frame over 256 bytes without a word.
        var wire = new MemoryStream();
        var transport = new dapps.client.Transport.Agw.AgwFrameTransport(wire);
        var data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

        await transport.WriteDataAsync(1, "N0AAA-9", "N0BBB-9", data, TestContext.Current.CancellationToken);

        wire.Position = 0;
        var reader = new dapps.client.Transport.Agw.AgwFrameTransport(wire);
        var frames = new List<AgwFrame>();
        for (var i = 0; i < frameSizes.Length; i++) frames.Add(await reader.ReadFrameAsync(TestContext.Current.CancellationToken));
        wire.Position.Should().Be(wire.Length, "nothing beyond the expected frames was written");
        frames.Select(f => f.Payload.Length).Should().Equal(frameSizes);
        frames.Should().AllSatisfy(f => { f.Kind.Should().Be('D'); f.Port.Should().Be(1); f.CallTo.Should().Be("N0BBB-9"); });
        frames.SelectMany(f => f.Payload).Should().Equal(data);
    }

    [Fact]
    public void HeaderLength_IsThirtySix()
    {
        AgwFrame.HeaderLength.Should().Be(36);
    }

    [Fact]
    public void WriteHeader_PutsFieldsAtSpecCorrectOffsets()
    {
        var frame = new AgwFrame(
            Port: 2,
            Kind: 'C',
            Pid: 0xF0,
            CallFrom: "M0LTE-7",
            CallTo: "G8BPQ-1",
            Payload: []);

        var buffer = new byte[36];
        frame.WriteHeader(buffer);

        buffer[0].Should().Be(2);                                                // Port
        buffer.AsSpan(1, 3).ToArray().Should().Equal(0, 0, 0);                   // filler
        buffer[4].Should().Be((byte)'C');                                        // DataKind
        buffer[5].Should().Be(0);                                                // filler
        buffer[6].Should().Be(0xF0);                                             // PID
        buffer[7].Should().Be(0);                                                // filler
        Encoding.ASCII.GetString(buffer.AsSpan(8, 7)).Should().Be("M0LTE-7");    // callfrom
        buffer.AsSpan(15, 3).ToArray().Should().Equal(0, 0, 0);                  // callfrom NUL pad
        Encoding.ASCII.GetString(buffer.AsSpan(18, 7)).Should().Be("G8BPQ-1");   // callto
        BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(28, 4)).Should().Be(0); // DataLength
    }

    [Fact]
    public void DataLength_IsLittleEndian()
    {
        var frame = new AgwFrame(0, 'D', 0xF0, "A", "B", new byte[256]);

        var buffer = new byte[36];
        frame.WriteHeader(buffer);

        // 256 = 0x00000100; LE: 00 01 00 00
        buffer.AsSpan(28, 4).ToArray().Should().Equal(0x00, 0x01, 0x00, 0x00);
    }

    [Fact]
    public void ToBytes_AppendsPayloadAfterHeader()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var frame = new AgwFrame(0, 'D', 0xF0, "A", "B", payload);

        var bytes = frame.ToBytes();

        bytes.Length.Should().Be(36 + 3);
        bytes.AsSpan(36).ToArray().Should().Equal(payload);
    }

    [Fact]
    public void ParseHeader_RoundTripsKindAndCallsigns()
    {
        var original = new AgwFrame(7, 'D', 0xF0, "M0LTE-7", "G8BPQ-1", []);
        var buffer = new byte[36];
        original.WriteHeader(buffer);

        var parsed = AgwFrame.ParseHeader(buffer, []);

        parsed.Port.Should().Be(7);
        parsed.Kind.Should().Be('D');
        parsed.Pid.Should().Be(0xF0);
        parsed.CallFrom.Should().Be("M0LTE-7");
        parsed.CallTo.Should().Be("G8BPQ-1");
    }

    [Fact]
    public void ReadDataLength_RecoversWrittenValue()
    {
        var frame = new AgwFrame(0, 'D', 0xF0, "A", "B", new byte[123]);
        var buffer = new byte[36];
        frame.WriteHeader(buffer);

        AgwFrame.ReadDataLength(buffer).Should().Be(123);
    }

    [Fact]
    public void WriteHeader_RejectsCallsignLongerThanTenChars()
    {
        var frame = new AgwFrame(0, 'C', 0xF0, "TOOLONGFORTHISFIELD", "X", []);
        var buffer = new byte[36];

        var act = () => frame.WriteHeader(buffer);
        act.Should().Throw<ArgumentException>();
    }

    // #279: the read side trusts nothing from the header before it has
    // checked it, and the write side never sends what BPQ would call corrupt.

    /// <summary>A frame's bytes with the header's DataLength (and nothing
    /// else) changed after the fact, as a corrupt or non-AGW stream would
    /// present it.</summary>
    private static byte[] WithDataLength(AgwFrame frame, int dataLength)
    {
        var bytes = frame.ToBytes();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), dataLength);
        return bytes;
    }

    [Theory]
    [InlineData(AgwFrameTransport.MaxReceivedDataLength + 1)]
    [InlineData(100_000_000)]
    [InlineData(-1)]
    public async Task ReadFrameAsync_ImplausibleDataLength_ThrowsBeforeAllocating(int dataLength)
    {
        var wire = new MemoryStream(WithDataLength(new AgwFrame(0, 'D', 0xF0, "A", "B", []), dataLength));
        var transport = new AgwFrameTransport(wire);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => transport.ReadFrameAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidDataException>();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(256 * 1024, "nothing is allocated on the header's say-so");
    }

    [Fact]
    public async Task ReadFrameAsync_LargestAcceptedDataLength_Reads()
    {
        var payload = new byte[AgwFrameTransport.MaxReceivedDataLength];
        var wire = new MemoryStream(new AgwFrame(0, 'G', 0, "", "", payload).ToBytes());

        var frame = await new AgwFrameTransport(wire).ReadFrameAsync(TestContext.Current.CancellationToken);

        frame.Payload.Length.Should().Be(AgwFrameTransport.MaxReceivedDataLength);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)'1')]
    [InlineData((byte)0xC4)]
    public async Task ReadFrameAsync_KindThatIsNotALetter_Throws(byte kind)
    {
        var bytes = new AgwFrame(0, 'D', 0xF0, "A", "B", [1, 2, 3]).ToBytes();
        bytes[4] = kind;
        var transport = new AgwFrameTransport(new MemoryStream(bytes));

        var act = () => transport.ReadFrameAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadFrameAsync_FramesForAPortOver64_AreDroppedWithOneWarning()
    {
        var wire = new MemoryStream([
            .. new AgwFrame(65, 'U', 0xF0, "G0XYZ", "DAPPS", "dropped"u8.ToArray()).ToBytes(),
            .. new AgwFrame(65, 'U', 0xF0, "G0XYZ", "DAPPS", "dropped too"u8.ToArray()).ToBytes(),
            .. new AgwFrame(65, 'S', 0xF0, "G0XYZ", "DAPPS", "and this"u8.ToArray()).ToBytes(),
            .. new AgwFrame(64, 'U', 0xF0, "G0XYZ", "DAPPS", "kept"u8.ToArray()).ToBytes(),
        ]);
        var logs = new CapturingLoggerFactory();
        var transport = new AgwFrameTransport(wire, logger: logs.CreateLogger("test"));

        var frame = await transport.ReadFrameAsync(TestContext.Current.CancellationToken);

        frame.Port.Should().Be(64);
        Encoding.ASCII.GetString(frame.Payload).Should().Be("kept");
        logs.Warnings.Should().ContainSingle("one warning per port, not one per frame").Which.Message.Should().Contain("port 65");
    }

    public static TheoryData<AgwFrame> FramesBpqWouldCallCorrupt => new()
    {
        new AgwFrame(65, 'G', 0, "", "", []),
        new AgwFrame(255, 'D', 0xF0, "A", "B", [1]),
        new AgwFrame(0, 'D', 0xF0, "A", "B", new byte[AgwFrameTransport.MaxFrameDataBytes + 1]),
        new AgwFrame(0, 'X', 0, "A", "", new byte[AgwFrameTransport.MaxFrameDataBytes + 1]),
        new AgwFrame(0, 'M', 0xF0, "A", "DAPPS", new byte[AgwFrameTransport.MaxUnprotoBytes + 1]),
        new AgwFrame(0, 'V', 0xF0, "A", "DAPPS", new byte[AgwFrameTransport.MaxUnprotoBytes + 1]),
    };

    [Theory]
    [MemberData(nameof(FramesBpqWouldCallCorrupt))]
    public async Task WriteFrameAsync_AFrameBpqWouldCallCorrupt_IsRefusedToTheCallerAndNothingIsSent(AgwFrame frame)
    {
        var wire = new MemoryStream();
        var transport = new AgwFrameTransport(wire);

        var act = () => transport.WriteFrameAsync(frame, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        wire.Length.Should().Be(0);

        // The transport is still usable afterwards.
        await transport.WriteFrameAsync(new AgwFrame(0, 'G', 0, "", "", []), TestContext.Current.CancellationToken);
        wire.Length.Should().Be(AgwFrame.HeaderLength);
    }

    public static TheoryData<AgwFrame> FramesAtTheLimits => new()
    {
        new AgwFrame((byte)AgwFrameTransport.MaxPort, 'G', 0, "", "", []),
        new AgwFrame(0, 'D', 0xF0, "A", "B", new byte[AgwFrameTransport.MaxFrameDataBytes]),
        new AgwFrame(0, 'M', 0xF0, "A", "DAPPS", new byte[AgwFrameTransport.MaxUnprotoBytes]),
    };

    [Theory]
    [MemberData(nameof(FramesAtTheLimits))]
    public async Task WriteFrameAsync_AFrameAtTheLimits_IsSent(AgwFrame frame)
    {
        var wire = new MemoryStream();

        await new AgwFrameTransport(wire).WriteFrameAsync(frame, TestContext.Current.CancellationToken);

        wire.Length.Should().Be(AgwFrame.HeaderLength + frame.Payload.Length);
    }
}
