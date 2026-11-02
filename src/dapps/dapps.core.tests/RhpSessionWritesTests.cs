using AwesomeAssertions;
using dapps.client.Transport;
using dapps.client.Transport.Rhp;
using dapps.client.Tx;
using RhpV2.Client;
using RhpV2.Client.Protocol;
using RhpV2.Client.Testing;

namespace dapps.core.tests;

/// <summary>
/// <see cref="RhpSessionWrites.Writer"/>, which every RHPv2 session (our
/// calls and the peers') writes through: no send over
/// <see cref="NodeSessionWrites.MaxBytes"/> (M0LTE/linbpq#85,
/// packet-net/dapps#425), one write's sends never mixed with another's,
/// and none at all while TX is stopped.
/// </summary>
public sealed class RhpSessionWritesTests
{
    private const int Handle = 42;

    [Fact]
    public void TheCap_IsNetRomsL4Payload()
    {
        NodeSessionWrites.MaxBytes.Should().Be(236, "LinBPQ's MaxL4Len: see M0LTE/linbpq#85 before raising it");
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(236, new[] { 236 })]
    [InlineData(237, new[] { 236, 1 })]
    [InlineData(600, new[] { 236, 236, 128 })]
    public async Task AWrite_GoesInSendsOfAtMost236Bytes(int length, int[] sizes)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = Server();
        await using var rhp = await RhpClient.ConnectAsync(server.Endpoint.Address.ToString(), server.Endpoint.Port, ct);
        var write = RhpSessionWrites.Writer(rhp, () => Handle, AlwaysOpenTxGate.Instance, "test");
        var data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

        await write(data, ct);

        var sends = await SendsAsync(server, length, ct);
        sends.Select(b => b.Length).Should().Equal(sizes);
        sends.SelectMany(b => b).Should().Equal(data);
    }

    [Fact]
    public async Task WritesMadeTogether_GoOneAfterTheOther_NeverMixed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = Server();
        await using var rhp = await RhpClient.ConnectAsync(server.Endpoint.Address.ToString(), server.Endpoint.Port, ct);
        var write = RhpSessionWrites.Writer(rhp, () => Handle, AlwaysOpenTxGate.Instance, "test");
        var writes = Enumerable.Range(0, 8).Select(i => Enumerable.Repeat((byte)('a' + i), 600).ToArray()).ToArray();

        await Task.WhenAll(writes.Select(w => Task.Run(() => write(w, ct), ct)));

        var sends = await SendsAsync(server, 8 * 600, ct);
        sends.Should().HaveCount(24);
        for (var i = 0; i < sends.Count; i += 3)
        {
            sends.Skip(i).Take(3).SelectMany(b => b).Distinct().Should().ContainSingle("a write's three sends go together");
        }
    }

    [Fact]
    public async Task WhileTxIsStopped_AWriteThrows_AndNothingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = Server();
        await using var rhp = await RhpClient.ConnectAsync(server.Endpoint.Address.ToString(), server.Endpoint.Port, ct);
        var write = RhpSessionWrites.Writer(rhp, () => Handle, new StoppedGate(), "RHP send on handle");

        var thrown = await FluentActions.Awaiting(() => write(new byte[600], ct)).Should().ThrowAsync<TxStoppedException>();
        thrown.Which.Message.Should().Contain("RHP send on handle 42").And.Contain("stopped for the test");
        Sends(server).Should().BeEmpty();
    }

    private static MockRhpServer Server()
    {
        // The mock only knows handles it opened itself; accept SENDs on
        // any handle.
        var server = new MockRhpServer
        {
            Handler = msg => msg is SendMessage send
                ? new SendReplyMessage { Handle = send.Handle, ErrCode = RhpErrorCode.Ok, ErrText = "Ok" }
                : null,
        };
        server.Start();
        return server;
    }

    /// <summary>What reached the server on the handle, once it has
    /// <paramref name="bytes"/> in all (or after a few seconds).</summary>
    private static async Task<List<byte[]>> SendsAsync(MockRhpServer server, int bytes, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var sends = Sends(server);
        while (sends.Sum(b => b.Length) < bytes && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
            sends = Sends(server);
        }
        return sends;
    }

    private static List<byte[]> Sends(MockRhpServer server) =>
        server.ReceivedFrames.ToArray().OfType<SendMessage>().Where(m => m.Handle == Handle)
            .Select(m => RhpDataEncoding.FromWireString(m.Data)).ToList();

    private sealed class StoppedGate : IDappsTxGate
    {
        public bool TxAllowed => false;
        public string? BlockReason => "stopped for the test";
    }
}
