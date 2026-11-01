using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// #431: a neighbour's Dial as callsign through POST /Neighbours: what is
/// taken, what is refused, and that it reaches the route the forwarder dials.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class NeighbourDialAsTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private MutableOptionsMonitor<SystemOptions> options = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-dialas-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.CreateTable<DbNeighbour>();
        }
        options = new MutableOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "MB7NPW-3", DefaultPort = 1 });
        database = new Database(NullLogger<Database>.Instance, options);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private NeighboursController Controller() => new(database, options);

    private static NeighbourModel ViaCalne(string callsign, string? dialAs) =>
        new(callsign, Port: 2, ConnectScript: $"C 3 {callsign}|DAPPSv1>", ConnectVia: "M1BFP-1", DialAs: dialAs);

    [Fact]
    public async Task ADialAsSsid_IsStored_Listed_AndOnTheRoute()
    {
        var ctrl = Controller();
        (await ctrl.Upsert(ViaCalne("GB7AGM-3", " mb7npw-4 "))).Should().BeOfType<NoContentResult>();

        var listed = (await ctrl.List()).Single();
        listed.DialAs.Should().Be("MB7NPW-4");

        var route = RouteBuilder.FromNeighbour((await database.GetNeighbour("GB7AGM-3"))!, 1);
        route.DialAs.Should().Be("MB7NPW-4");
        route.DialCallsign.Should().Be("M1BFP-1");
        route.SourceCallsign("MB7NPW-3").Should().Be("MB7NPW-4");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("mb7npw-3")]
    public async Task BlankOrOurOwnCallsign_IsTheDefault(string? dialAs)
    {
        var ctrl = Controller();
        (await ctrl.Upsert(ViaCalne("GB7AGM-3", dialAs))).Should().BeOfType<NoContentResult>();

        (await database.GetNeighbour("GB7AGM-3"))!.DialAs.Should().BeNull();
        RouteBuilder.FromNeighbour((await database.GetNeighbour("GB7AGM-3"))!, 1).SourceCallsign("MB7NPW-3").Should().Be("MB7NPW-3");
    }

    [Fact]
    public async Task ClearingIt_GoesBackToTheDefault()
    {
        var ctrl = Controller();
        await ctrl.Upsert(ViaCalne("GB7AGM-3", "MB7NPW-4"));
        (await ctrl.Upsert(ViaCalne("GB7AGM-3", null))).Should().BeOfType<NoContentResult>();
        (await database.GetNeighbour("GB7AGM-3"))!.DialAs.Should().BeNull();
    }

    [Theory]
    [InlineData("G4ABC-4", "an SSID of this node's callsign")]
    [InlineData("MB7NPW", "no SSID")]
    [InlineData("MB7NPW-0", "no SSID")]
    [InlineData("MB7NPW-16", "Callsign must be")]
    [InlineData("MB7 NPW", "Callsign must be")]
    public async Task ACallsignThatIsNotAnSsidOfOurs_IsRefused(string dialAs, string reason)
    {
        var result = await Controller().Upsert(ViaCalne("GB7AGM-3", dialAs));

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
            .Which.Should().StartWith("Dial as: ").And.Contain(reason);
        (await database.GetNeighbours()).Should().BeEmpty();
    }

    [Fact]
    public async Task ANeighboursCallsign_IsRefused()
    {
        var ctrl = Controller();
        await ctrl.Upsert(new NeighbourModel("MB7NPW-4", Port: 1));

        var result = await ctrl.Upsert(ViaCalne("GB7AGM-3", "MB7NPW-4"));

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
            .Which.Should().Contain("is a neighbour's callsign");
    }

    [Fact]
    public async Task AUdpNeighbour_IsRefused()
    {
        var result = await Controller().Upsert(new NeighbourModel("GB7AGM-3", Port: 1, UdpEndpoint: "10.0.0.1:1880", DialAs: "MB7NPW-4"));

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
            .Which.Should().Contain("not over UDP");
    }

    [Fact]
    public async Task WithNoCallsignSetYet_ItIsRefused()
    {
        options.Set(new SystemOptions { Callsign = DbStartup.PlaceholderCallsign });

        var result = await Controller().Upsert(ViaCalne("GB7AGM-3", "MB7NPW-4"));

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
            .Which.Should().Contain("set this node's callsign first");
    }

    [Fact]
    public async Task TwoNeighboursMayShareOne()
    {
        var ctrl = Controller();
        (await ctrl.Upsert(ViaCalne("GB7AGM-3", "MB7NPW-4"))).Should().BeOfType<NoContentResult>();
        (await ctrl.Upsert(ViaCalne("GB7LOX-3", "MB7NPW-4"))).Should().BeOfType<NoContentResult>();
    }

    [Theory]
    [InlineData("MB7NPW-4", "MB7NPW-3", null)]
    [InlineData("MB7NPW-4", "MB7NPW", null)]
    [InlineData("MB7NPW-4", "G4ABC-3", "not an SSID of this node's callsign G4ABC-3")]
    public void StillOurs_ChecksTheBaseCallsign(string dialAs, string ours, string? error)
    {
        var result = DialAsCallsign.StillOurs(dialAs, ours);
        if (error is null) result.Should().BeNull();
        else result.Should().Contain(error);
    }
}
