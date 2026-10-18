using System.Text.Json;
using System.Text.Json.Serialization;

namespace dapps.core.Controllers;

/// <summary>
/// Writes every DateTime as UTC with a trailing Z. The daemon only ever
/// stores UTC, but sqlite-net hands stored values back with
/// DateTimeKind.Unspecified, which System.Text.Json writes with no zone,
/// and the dashboard's <c>new Date(...)</c> then reads it as browser-local
/// time (an hour out in BST, #238, #362). Unspecified is taken as UTC;
/// Local is converted. Applies to DateTime? too.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    /// <summary>Adds this converter to the controllers' JSON settings
    /// (web defaults, so camelCase). JSON a controller writes by hand,
    /// such as the <c>/Events/inbound</c> stream, uses those same
    /// settings rather than a copy of them.</summary>
    public static void Configure(JsonSerializerOptions options) => options.Converters.Add(new UtcDateTimeJsonConverter());

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => AsUtc(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(AsUtc(value));

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
