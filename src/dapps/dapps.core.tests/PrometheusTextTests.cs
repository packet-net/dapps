using System.Globalization;
using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

/// <summary>
/// The text exposition format is what a scraper parses, so pin the
/// shape: HELP and TYPE lines before the samples, label values escaped,
/// numbers written the same under every locale.
/// </summary>
public sealed class PrometheusTextTests
{
    [Fact]
    public void Family_WritesHelpTypeThenSamples()
    {
        var text = new PrometheusText()
            .Counter("dapps_probes_total", "Neighbour probes by outcome.")
            .Sample("dapps_probes_total", 3, ("result", "success"))
            .Sample("dapps_probes_total", 1, ("result", "failure"))
            .Gauge("dapps_uptime_seconds", "Seconds up.", 12.5)
            .ToString();

        text.Should().Be(
            "# HELP dapps_probes_total Neighbour probes by outcome.\n" +
            "# TYPE dapps_probes_total counter\n" +
            "dapps_probes_total{result=\"success\"} 3\n" +
            "dapps_probes_total{result=\"failure\"} 1\n" +
            "# HELP dapps_uptime_seconds Seconds up.\n" +
            "# TYPE dapps_uptime_seconds gauge\n" +
            "dapps_uptime_seconds 12.5\n");
    }

    [Fact]
    public void LabelValues_AreEscaped()
    {
        var text = new PrometheusText()
            .Gauge("x", "help")
            .Sample("x", 1, ("neighbour", "G7\"QUOTE\\BACK\nLINE"))
            .ToString();

        text.Should().Contain("x{neighbour=\"G7\\\"QUOTE\\\\BACK\\nLINE\"} 1\n");
    }

    [Fact]
    public void Numbers_UseInvariantCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            // A locale with a comma decimal separator must not leak into
            // the document; "1,5" is not a Prometheus number.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var text = new PrometheusText().Gauge("x", "help", 1.5).ToString();
            text.Should().EndWith("x 1.5\n");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void UnixSeconds_CountsFromTheEpoch()
    {
        PrometheusText.UnixSeconds(new DateTime(1970, 1, 1, 0, 1, 0, DateTimeKind.Utc)).Should().Be(60);
    }
}
