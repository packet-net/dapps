using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Wire/round-trip tests for ConnectScript serialisation, plus
/// behaviour tests for ConnectScriptRunner driving a fake duplex
/// stream. The runner drives the transport-level expect/respond
/// engine that lets DAPPS reach a far-end node through a chain of
/// non-DAPPS intermediate packet nodes.
/// </summary>
public class ConnectScriptTests
{
    [Fact]
    public void ParseLines_ThreeStepChainWithFinalDappsPrompt_RoundTripsCleanly()
    {
        var text = "C G0NODE2|Connected to G0NODE2\nC G0NODE3|Connected to G0NODE3\nDAPPS|DAPPSv1>|60";
        var script = ConnectScript.ParseLines(text);

        script.Should().NotBeNull();
        script!.Steps.Should().HaveCount(3);
        script.Steps[0].Should().Be(new ConnectScriptStep("C G0NODE2", "Connected to G0NODE2"));
        script.Steps[2].Should().Be(new ConnectScriptStep("DAPPS", "DAPPSv1>", 60));
        script.EndsOnDappsPrompt.Should().BeTrue();
        script.ToLines().Should().Be(text);
    }

    [Fact]
    public void ParseLines_BlankLinesAndComments_AreSkipped()
    {
        var text = "# my chain\n\nC G0NODE2|Connected to G0NODE2\n\n# next hop\nDAPPS|DAPPSv1>";
        var script = ConnectScript.ParseLines(text)!;
        script.Steps.Should().HaveCount(2);
    }

    [Fact]
    public void ParseLines_EmptyOrWhitespace_ReturnsNull()
    {
        ConnectScript.ParseLines(null).Should().BeNull();
        ConnectScript.ParseLines("").Should().BeNull();
        ConnectScript.ParseLines("   \n  \n").Should().BeNull();
    }

    [Fact]
    public void ParseLines_MalformedLine_Throws()
    {
        var act = () => ConnectScript.ParseLines("just one field");
        act.Should().Throw<FormatException>().WithMessage("*line 1*");
    }

    [Fact]
    public void ParseLines_BadTimeout_Throws()
    {
        var act = () => ConnectScript.ParseLines("DAPPS|DAPPSv1>|nope");
        act.Should().Throw<FormatException>().WithMessage("*timeout*");
    }

    [Fact]
    public void Json_RoundTripPreservesEverything()
    {
        var script = new ConnectScript([
            new("C G0A", "Connected to G0A"),
            new("DAPPS", "DAPPSv1>", 60),
        ]);
        var json = script.ToJson();
        var parsed = ConnectScript.FromJson(json);
        parsed.Should().BeEquivalentTo(script);
    }

    [Fact]
    public void ResolveFirstHop_LeadingBareConnect_DialsThatNodeAndDropsTheStep()
    {
        var script = ConnectScript.ParseLines("C GB7BDH|Connected\nC 14 GB7AGM-3|Connected\nDAPPS|DAPPSv1>");

        var (dial, rest) = ConnectScript.ResolveFirstHop("GB7AGM-3", script);

        dial.Should().Be("GB7BDH");
        rest!.ToLines().Should().Be("C 14 GB7AGM-3|Connected\nDAPPS|DAPPSv1>");
    }

    [Theory]
    [InlineData("connect gb7bdh-7", "GB7BDH-7")]
    [InlineData("  C   G0NODE  ", "G0NODE")]
    public void ResolveFirstHop_AcceptsConnectSpellingsAndSsids(string send, string expected)
    {
        var script = new ConnectScript([new(send, "Connected"), new("DAPPS", "DAPPSv1>")]);

        ConnectScript.ResolveFirstHop("N0THEM", script).DialCallsign.Should().Be(expected);
    }

    [Theory]
    [InlineData("C 14 GB7AGM-3")] // a port number: typed at a node prompt
    [InlineData("DAPPS")]
    [InlineData("C G0NODE2 V G0DIGI")] // via digipeaters: not a plain dial
    [InlineData("C G0NODE2")] // seven characters: not a callsign
    public void ResolveFirstHop_OtherFirstSteps_DialTheTargetAndKeepTheScript(string send)
    {
        var script = new ConnectScript([new(send, "Connected"), new("DAPPS", "DAPPSv1>")]);

        var (dial, rest) = ConnectScript.ResolveFirstHop("N0THEM", script);

        dial.Should().Be("N0THEM");
        rest.Should().BeSameAs(script);
    }

    [Fact]
    public void ResolveFirstHop_FirstStepExpectingThePrompt_IsKept()
    {
        var script = new ConnectScript([new("C G0NODE2", "DAPPSv1>")]);

        var (dial, rest) = ConnectScript.ResolveFirstHop("N0THEM", script);

        dial.Should().Be("N0THEM");
        rest.Should().BeSameAs(script);
    }

    [Fact]
    public void ResolveFirstHop_OnlyStep_LeavesNoScript()
    {
        var script = new ConnectScript([new("C G0NODE", "Connected")]);

        ConnectScript.ResolveFirstHop("N0THEM", script).Should().Be(("G0NODE", (ConnectScript?)null));
    }

    [Fact]
    public void ResolveFirstHop_NoScript_DialsTheTarget()
    {
        ConnectScript.ResolveFirstHop("N0THEM", null).Should().Be(("N0THEM", (ConnectScript?)null));
    }

    [Fact]
    public async Task Runner_HappyPath_PlaysAllStepsAndReturnsTranscript()
    {
        var script = new ConnectScript([
            new("C G0A", "Connected to G0A"),
            new("DAPPS", "DAPPSv1>"),
        ]);
        // The runner sends "C G0A\r" then expects "Connected to G0A".
        // Then "DAPPS\r" then expects "DAPPSv1>".
        // We feed scripted responses on the read side.
        var responses = new[]
        {
            "Welcome to G0A node\r\n*** Connected to G0A\r\nNODE:G0A> ",
            "Entering DAPPS slot...\r\nDAPPSv1>\n",
        };
        var stream = new ScriptedDuplexStream(responses);

        var transcript = await ConnectScriptRunner.RunAsync(stream, script, NullLogger.Instance, CancellationToken.None);

        // Sent bytes should be the two send lines, each terminated with \r.
        stream.SentText.Should().Be("C G0A\rDAPPS\r");
        transcript.Should().Contain("Welcome to G0A");
        transcript.Should().Contain("DAPPSv1>");
    }

    [Fact]
    public async Task Runner_TimeoutOnExpect_ThrowsConnectScriptException()
    {
        var script = new ConnectScript([
            new("C G0A", "Connected to G0A", TimeoutSeconds: 1),
        ]);
        // Server says nothing relevant, then *blocks* (not EOF).
        // Real bearer streams stay open even when the peer falls
        // silent - the runner relies on its per-step timeout to
        // surface that as a script failure rather than waiting
        // forever.
        var stream = new ScriptedDuplexStream(["ehlo, nothing useful here\r\n"], blockAfterCanned: true);

        var act = async () => await ConnectScriptRunner.RunAsync(stream, script, NullLogger.Instance, CancellationToken.None);
        await act.Should().ThrowAsync<ConnectScriptException>().WithMessage("*timed out*");
    }

    [Fact]
    public async Task Runner_StreamClosesMidScript_ThrowsEndOfStream()
    {
        var script = new ConnectScript([
            new("C G0A", "Connected to G0A"),
        ]);
        var stream = new ScriptedDuplexStream(["partial response, then EOF"]);

        var act = async () => await ConnectScriptRunner.RunAsync(stream, script, NullLogger.Instance, CancellationToken.None);
        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public async Task Runner_SubstringBoundaryAcrossReads_StillMatches()
    {
        // The expect "DAPPSv1>" lands split across two reads. The
        // sliding-window matcher should still find it.
        var script = new ConnectScript([new("DAPPS", "DAPPSv1>")]);
        var stream = new ScriptedDuplexStream([
            "noise\r\nDAPP",   // first chunk: "DAPP" (incomplete)
            "Sv1>\n",          // second chunk: completes "DAPPSv1>"
        ]);

        var transcript = await ConnectScriptRunner.RunAsync(stream, script, NullLogger.Instance, CancellationToken.None);

        transcript.Should().Contain("DAPPSv1>");
    }

    /// <summary>Memory-only duplex stream that serves a sequence of
    /// pre-canned response chunks on read and records everything
    /// written for assertion.</summary>
    private sealed class ScriptedDuplexStream : Stream
    {
        private readonly Queue<byte[]> reads;
        private readonly System.Text.StringBuilder sent = new();
        private readonly bool blockAfterCanned;
        private byte[]? current;
        private int pos;

        public ScriptedDuplexStream(IEnumerable<string> responses, bool blockAfterCanned = false)
        {
            reads = new Queue<byte[]>(responses.Select(System.Text.Encoding.UTF8.GetBytes));
            this.blockAfterCanned = blockAfterCanned;
        }

        public string SentText => sent.ToString();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Yield();
            if (current is null || pos >= current.Length)
            {
                if (reads.Count == 0)
                {
                    if (blockAfterCanned)
                    {
                        // Block until the caller's CancellationToken (the
                        // step timeout) fires, then surface as cancellation
                        // - the runner translates that into a timeout.
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    return 0;
                }
                current = reads.Dequeue();
                pos = 0;
            }
            var n = Math.Min(count, current.Length - pos);
            Array.Copy(current, pos, buffer, offset, n);
            pos += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            if (current is null || pos >= current.Length)
            {
                if (reads.Count == 0)
                {
                    if (blockAfterCanned)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    return 0;
                }
                current = reads.Dequeue();
                pos = 0;
            }
            var n = Math.Min(buffer.Length, current.Length - pos);
            current.AsSpan(pos, n).CopyTo(buffer.Span);
            pos += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            sent.Append(System.Text.Encoding.UTF8.GetString(buffer, offset, count));
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            sent.Append(System.Text.Encoding.UTF8.GetString(buffer.Span));
            return ValueTask.CompletedTask;
        }
    }
}
