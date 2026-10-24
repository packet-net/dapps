using System.Globalization;
using System.Text;

namespace dapps.core.Services;

/// <summary>
/// Builds a document in the Prometheus text exposition format
/// (version 0.0.4): a <c># HELP</c> and <c># TYPE</c> line per metric
/// family, then one sample line per label set. Hand-rolled because the
/// node exposes a few dozen series and nothing more; a client library
/// would add a dependency for the escaping rules below and little else.
///
/// Metric and label names are the caller's responsibility (they are
/// code, not data). Label values are escaped as the format requires:
/// backslash, double quote and newline. Every number is written with
/// the invariant culture, so a node running under a locale with a
/// comma decimal separator still produces a parseable document.
/// </summary>
public sealed class PrometheusText
{
    /// <summary>The content type a scraper expects on the response.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private readonly StringBuilder sb = new();

    /// <summary>Starts a counter family. Add samples with <see cref="Sample"/>.</summary>
    public PrometheusText Counter(string name, string help) => Family(name, "counter", help);

    /// <summary>Starts a gauge family. Add samples with <see cref="Sample"/>.</summary>
    public PrometheusText Gauge(string name, string help) => Family(name, "gauge", help);

    /// <summary>A gauge with one unlabelled sample, the common case.</summary>
    public PrometheusText Gauge(string name, string help, double value) => Gauge(name, help).Sample(name, value);

    /// <summary>A counter with one unlabelled sample, the common case.</summary>
    public PrometheusText Counter(string name, string help, double value) => Counter(name, help).Sample(name, value);

    private PrometheusText Family(string name, string type, string help)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        return this;
    }

    /// <summary>One sample line for the family started last.</summary>
    public PrometheusText Sample(string name, double value, params (string Name, string Value)[] labels)
    {
        sb.Append(name);
        if (labels.Length > 0)
        {
            sb.Append('{');
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(labels[i].Name).Append("=\"").Append(EscapeLabel(labels[i].Value)).Append('"');
            }
            sb.Append('}');
        }
        sb.Append(' ').Append(Format(value)).Append('\n');
        return this;
    }

    public override string ToString() => sb.ToString();

    /// <summary>Seconds since the Unix epoch, the unit Prometheus
    /// expects for a <c>_timestamp_seconds</c> gauge.</summary>
    public static double UnixSeconds(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    private static string Format(double value)
    {
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        if (double.IsNaN(value)) return "NaN";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string EscapeHelp(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n");
}
