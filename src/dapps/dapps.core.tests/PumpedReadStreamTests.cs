using System.IO.Pipes;
using System.Text;
using AwesomeAssertions;
using dapps.client.Transport;

namespace dapps.core.tests;

public sealed class PumpedReadStreamTests
{
    [Fact]
    public async Task ACancelledWait_LosesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (writer, reader) = Pair();
        await using var _w = writer;
        using var pumped = new PumpedReadStream(reader);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            var act = async () => await pumped.WaitForDataAsync(cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>("nothing has been sent yet");
        }

        await writer.WriteAsync("pending\n"u8.ToArray(), ct);
        (await pumped.WaitForDataAsync(ct)).Should().BeTrue();
        (await ReadAllAvailableAsync(pumped, 8, ct)).Should().Be("pending\n");
    }

    [Fact]
    public async Task WaitingDoesNotConsume_AndReadsComeBackInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (writer, reader) = Pair();
        await using var _w = writer;
        using var pumped = new PumpedReadStream(reader);

        await writer.WriteAsync("send abc\nack abc\n"u8.ToArray(), ct);
        (await pumped.WaitForDataAsync(ct)).Should().BeTrue();
        (await pumped.WaitForDataAsync(ct)).Should().BeTrue("waiting twice still leaves the bytes there");

        (await ReadAllAvailableAsync(pumped, 17, ct)).Should().Be("send abc\nack abc\n");
    }

    [Fact]
    public async Task TheLinkEnding_IsReportedAsNoData()
    {
        var ct = TestContext.Current.CancellationToken;
        var (writer, reader) = Pair();
        using var pumped = new PumpedReadStream(reader);

        await writer.DisposeAsync();

        (await pumped.WaitForDataAsync(ct)).Should().BeFalse();
        (await pumped.ReadAsync(new byte[4], ct)).Should().Be(0);
    }

    private static (Stream Writer, Stream Reader) Pair()
    {
        var server = new AnonymousPipeServerStream(PipeDirection.Out);
        var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        return (server, client);
    }

    private static async Task<string> ReadAllAvailableAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
