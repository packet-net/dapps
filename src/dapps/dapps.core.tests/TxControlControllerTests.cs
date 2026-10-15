using AwesomeAssertions;
using dapps.core.Controllers;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// PR 2 wiring of the master TX-stop button. Confirms POST /TxControl/stop
/// flips SystemOptions.TxEnabled and that the gate reflects it; ensures
/// every toggle is audited regardless of whether the state changed; and
/// pins the redirect-to-Referer behaviour the dashboard relies on.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class TxControlControllerTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-txctl-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using var c = DbInfo.GetConnection();
        c.CreateTable<DbSystemOption>();
        c.CreateTable<DbTransmission>();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StopAndResume_KeepEveryOtherSetting()
    {
        // STOP used to save a hand-written copy of the settings that left
        // out the MeshCore ones, so it turned MeshCore off.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbSystemOption { Option = "MeshCoreEnabled", Value = "True" });
            c.Insert(new DbSystemOption { Option = "MeshCorePort", Value = "/dev/ttyACM3" });
            c.Insert(new DbSystemOption { Option = "MeshCoreChannelPsk", Value = "not-the-default" });
            c.Insert(new DbSystemOption { Option = "RouteGossipStalenessHours", Value = "0" });
        }
        var (ctrl, store, _, _) = BuildController();

        await ctrl.Stop();
        store.CurrentValue.TxEnabled.Should().BeFalse();
        store.CurrentValue.MeshCoreEnabled.Should().BeTrue();
        store.CurrentValue.MeshCorePort.Should().Be("/dev/ttyACM3");
        store.CurrentValue.MeshCoreChannelPsk.Should().Be("not-the-default");
        store.CurrentValue.RouteGossipStalenessHours.Should().Be(0);

        await ctrl.Resume();
        store.CurrentValue.TxEnabled.Should().BeTrue();
        store.CurrentValue.MeshCoreEnabled.Should().BeTrue();
        store.CurrentValue.MeshCorePort.Should().Be("/dev/ttyACM3");
    }

    [Fact]
    public async Task Stop_FlipsTxEnabledFalse_AndGateReports()
    {
        var (ctrl, store, gate, _) = BuildController();

        // Sanity: starts allowed.
        gate.TxAllowed.Should().BeTrue();
        store.CurrentValue.TxEnabled.Should().BeTrue();

        var result = await ctrl.Stop();
        result.Should().BeAssignableTo<RedirectResult>();

        store.CurrentValue.TxEnabled.Should().BeFalse();
        gate.TxAllowed.Should().BeFalse();
        gate.LocalAllowed.Should().BeFalse();
        gate.BlockReason.Should().Contain("master TX-stop");
    }

    [Fact]
    public async Task Resume_FlipsTxEnabledBackToTrue()
    {
        var (ctrl, store, gate, _) = BuildController();

        await ctrl.Stop();
        store.CurrentValue.TxEnabled.Should().BeFalse();

        await ctrl.Resume();
        store.CurrentValue.TxEnabled.Should().BeTrue();
        gate.TxAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_AndResume_BothLeaveAuditTrail()
    {
        var (ctrl, _, _, _) = BuildController();

        await ctrl.Stop();
        await ctrl.Resume();

        var rows = await CountAuditRows();
        rows.Should().Be(2, "every toggle leaves a tx-control audit row");
    }

    [Fact]
    public async Task Stop_WhenAlreadyStopped_LogsNoOpAuditAndStaysStopped()
    {
        var (ctrl, store, _, _) = BuildController();

        await ctrl.Stop();
        await ctrl.Stop();

        store.CurrentValue.TxEnabled.Should().BeFalse();
        var rows = await CountAuditRows();
        rows.Should().Be(2, "the second stop is a no-op but still audited so the intent is logged");
    }

    [Fact]
    public async Task GetStatus_ReturnsLiveGateState()
    {
        var (ctrl, _, _, _) = BuildController();

        var initial = ctrl.GetStatus();
        initial.TxAllowed.Should().BeTrue();
        initial.LocalAllowed.Should().BeTrue();
        initial.BlockReason.Should().BeNull();

        await ctrl.Stop();

        var afterStop = ctrl.GetStatus();
        afterStop.TxAllowed.Should().BeFalse();
        afterStop.LocalAllowed.Should().BeFalse();
        afterStop.BlockReason.Should().NotBeNull();
    }

    [Fact]
    public async Task Stop_PreservesOtherSystemOptions()
    {
        var (ctrl, store, _, _) = BuildController();

        // Seed a non-default value to confirm the controller's clone
        // round-trips it instead of resetting the row to defaults.
        var seeded = store.CurrentValue;
        seeded.Callsign = "M0LTE-9";
        seeded.NodeHost = "bpq.local";
        seeded.MqttPort = 1884;
        await store.SaveAsync(seeded);

        await ctrl.Stop();

        var after = store.CurrentValue;
        after.TxEnabled.Should().BeFalse();
        after.Callsign.Should().Be("M0LTE-9");
        after.NodeHost.Should().Be("bpq.local");
        after.MqttPort.Should().Be(1884);
    }

    private (TxControlController ctrl, SystemOptionsStore store, SystemOptionsBackedTxGate gate, TransmissionAuditService audit)
        BuildController()
    {
        var store = new SystemOptionsStore(NullLogger<SystemOptionsStore>.Instance);
        var remote = new OpenTxKillSwitchSignal();
        var gate = new SystemOptionsBackedTxGate(store, remote);
        var audit = new TransmissionAuditService(
            NullLogger<TransmissionAuditService>.Instance,
            store,
            mqttBroker: null!,
            timeProvider: new FakeTimeProvider());

        var ctrl = new TxControlController(store, gate, audit);
        // Razor passes a Referer header; controller's Redirect uses
        // it. Default to "/" when absent (the production fallback).
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return (ctrl, store, gate, audit);
    }

    private static async Task<int> CountAuditRows()
    {
        var connection = DbInfo.GetAsyncConnection();
        var rows = await connection.QueryAsync<DbTransmission>("select * from transmissions where Kind='tx-control';");
        return rows.Count;
    }
}
