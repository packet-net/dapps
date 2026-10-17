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
