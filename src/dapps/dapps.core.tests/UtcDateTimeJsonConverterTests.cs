using System.Text.Json;
using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// #238, #362: times read back from SQLite have no kind, and without the
/// converter they went out with no zone, so the dashboard read them as
/// local time.
/// </summary>
public sealed class UtcDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Web)
    {
        Converters = { new UtcDateTimeJsonConverter() },
    };

    [Fact]
    public void ATimeReadBackFromSqlite_IsWrittenAsUtcWithAZ()
    {
        var at = new DateTime(2026, 10, 17, 14, 27, 9, DateTimeKind.Utc);
        using var db = new SQLiteConnection(":memory:");
        db.CreateTable<DbProbedNode>();
        db.Insert(new DbProbedNode { Callsign = "M0ABC", LastProbedAt = at, LastSuccessAt = null });
        var row = db.Table<DbProbedNode>().Single();
        row.LastProbedAt!.Value.Kind.Should().Be(DateTimeKind.Unspecified, "this is what sqlite-net hands back");

        var json = JsonSerializer.Serialize(row, Options);

        json.Should().Contain("\"lastProbedAt\":\"2026-10-17T14:27:09Z\"");
        json.Should().Contain("\"lastSuccessAt\":null");
    }

    // Only bites where local time is not UTC: on a UTC machine (CI,
    // studybox) it passes with the Local branch broken. Run it with
    // TZ=Europe/London to check that branch.
    [Fact]
    public void LocalTimes_AreConverted_AndUtcTimesAreLeftAlone()
    {
        var utc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        JsonSerializer.Serialize(utc, Options).Should().Be("\"2026-07-01T12:00:00Z\"");
        JsonSerializer.Serialize(utc.ToLocalTime(), Options).Should().Be("\"2026-07-01T12:00:00Z\"");
    }

    [Theory]
    [InlineData("\"2026-07-01T12:00:00\"")]
    [InlineData("\"2026-07-01T12:00:00Z\"")]
    [InlineData("\"2026-07-01T13:00:00+01:00\"")]
    public void Reading_GivesUtc(string json)
    {
        var value = JsonSerializer.Deserialize<DateTime>(json, Options);

        value.Kind.Should().Be(DateTimeKind.Utc);
        value.Should().Be(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));
    }
}
