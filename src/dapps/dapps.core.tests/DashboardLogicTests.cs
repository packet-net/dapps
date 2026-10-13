using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Pages;

namespace dapps.core.tests;

/// <summary>
/// Pure-function helpers on <see cref="IndexModel"/> that decide what
/// the dashboard shows. These map the queue's internal flags
/// (Forwarded / LocallyDelivered / destination-is-local) into the
/// human-readable status string the table renders, and format the
/// row's age. Logic is tested here rather than via the rendered HTML
/// because Razor render-coverage doesn't add value when the wiring
/// is already exercised by smoke tests.
/// </summary>
public class DashboardLogicTests
{
    [Fact]
    public void MessageStatus_LocalUndelivered_PendingLocal()
    {
        var m = new DbMessage { Destination = "myapp@N0CALL", LocallyDelivered = false };
        IndexModel.MessageStatus(m, "N0CALL").Label.Should().Be("pending local");
    }

    [Fact]
    public void MessageStatus_LocalDelivered_Delivered()
    {
        var m = new DbMessage { Destination = "myapp@N0CALL", LocallyDelivered = true };
        IndexModel.MessageStatus(m, "N0CALL").Label.Should().Be("delivered");
    }

    [Fact]
    public void MessageStatus_OtherSsidOfOurCallsign_IsRemote()
    {
        // "myapp@N0CALL-3" - same base callsign as us, different SSID,
        // so another node's mail (#239).
        var m = new DbMessage { Destination = "myapp@N0CALL-3", Forwarded = false };
        IndexModel.MessageStatus(m, "N0CALL").Label.Should().Be("pending");
    }

    [Fact]
    public void MessageStatus_RemoteUnforwarded_PendingOut()
    {
        var m = new DbMessage { Destination = "myapp@N0DEST", Forwarded = false };
        IndexModel.MessageStatus(m, "N0CALL").Label.Should().Be("pending");
    }

    [Fact]
    public void MessageStatus_RemoteForwarded_Forwarded()
    {
        var m = new DbMessage { Destination = "myapp@N0DEST", Forwarded = true };
        IndexModel.MessageStatus(m, "N0CALL").Label.Should().Be("forwarded");
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(89, "89s")]
    public void Age_Seconds(int seconds, string expected)
    {
        var t = DateTime.UtcNow.AddSeconds(-seconds);
        IndexModel.Age(t).Should().Be(expected);
    }

    [Fact]
    public void Age_MinutesAndHours()
    {
        IndexModel.Age(DateTime.UtcNow.AddMinutes(-5)).Should().EndWith("m");
        IndexModel.Age(DateTime.UtcNow.AddHours(-3)).Should().EndWith("h");
        IndexModel.Age(DateTime.UtcNow.AddDays(-2)).Should().EndWith("d");
    }
}
