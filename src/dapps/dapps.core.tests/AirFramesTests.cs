using AwesomeAssertions;
using dapps.core.tests.Integration;

namespace dapps.core.tests;

/// <summary>
/// The end-to-end tests count what went over the air with
/// <see cref="AirFrames"/>. A retransmission must count once (#352), and
/// anything DAPPS really did twice must still count twice, or the tests
/// can no longer catch it.
/// </summary>
public sealed class AirFramesTests
{
    private const string A = "N0AAA-9", B = "N0BBB-9";

    private static (string, string) FromA(string header, string text = "") =>
        ("A", $"2:Fm {A} To {B} <{header}>[12:00:00]" + (text.Length > 0 ? "\n" + text : ""));

    private static (string, string) FromB(string header, string text = "") =>
        ("B", $"2:Fm {B} To {A} <{header}>[12:00:00]" + (text.Length > 0 ? "\n" + text : ""));

    private static int Count(List<(string, string)> air, string side, string text) =>
        AirFrames.SentBy(air, side, A, B).Count(f => f.Contains(text, StringComparison.Ordinal));

    /// <summary>One probe: connect, peers, B's list, A hangs up.</summary>
    private static IEnumerable<(string, string)> Probe() =>
    [
        FromA("C C P"),
        FromB("UA R F"),
        FromA("I C S0 R0", "peers"),
        FromB("I C S0 R1", $"peer {A} source=n port=2"),
        FromA("RR R R1"),
        FromA("D C P"),
        FromB("UA R F"),
    ];

    [Fact]
    public void TwoProbes_OnTwoConnections_CountTwice()
    {
        // Each connection starts at N(S) 0, so both probes' "peers" frames
        // are S0 with the same text. They are two probes, not one resent.
        var air = Probe().Concat(Probe()).ToList();

        Count(air, "A", "\npeers").Should().Be(2);
        Count(air, "B", $"peer {A}").Should().Be(2);
        AirFrames.Connects(air, "A", A, B).Should().Be(2);
    }

    [Fact]
    public void AFrameResentBeforeItWasAcknowledged_CountsOnce()
    {
        List<(string, string)> air =
        [
            FromA("C C P"),
            FromB("UA R F"),
            FromA("I C S0 R0", "peers"),
            FromA("I C P S0 R0", "peers"),     // the ack was slow coming
            FromB("I C S0 R1", $"peer {A} source=n port=2"),
        ];

        Count(air, "A", "\npeers").Should().Be(1);
    }

    [Fact]
    public void TheSameFrameAgainAfterItWasAcknowledged_CountsTwice()
    {
        // Eight frames on, N(S) is 0 again: a frame with the same text after
        // B acknowledged the first is DAPPS sending it twice.
        List<(string, string)> air = [FromA("C C P"), FromB("UA R F"), FromA("I C S0 R0", "ping 1")];
        for (var n = 1; n < 8; n++)
        {
            air.Add(FromB($"RR R R{n}"));
            air.Add(FromA($"I C S{n} R0", $"filler {n}"));
        }
        air.Add(FromB("RR R R0"));
        air.Add(FromA("I C S0 R0", "ping 1"));

        Count(air, "A", "ping 1").Should().Be(2);
    }

    [Fact]
    public void ASabmRetriedWithNoAnswer_IsOneConnect_ButARedialAfterADmIsTwo()
    {
        List<(string, string)> retried = [FromA("C C P"), FromA("C C P"), FromB("UA R F")];
        AirFrames.Connects(retried, "A", A, B).Should().Be(1);

        List<(string, string)> redialled = [FromA("C C P"), FromB("DM R F"), FromA("C C P"), FromB("UA R F")];
        AirFrames.Connects(redialled, "A", A, B).Should().Be(2);
    }
}
