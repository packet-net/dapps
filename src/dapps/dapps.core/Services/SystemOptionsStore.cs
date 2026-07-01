using System.Globalization;
using dapps.client;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.Services;

/// <summary>
/// Canonical reader/writer for the persisted <see cref="SystemOptions"/>
/// row set, and the in-process <see cref="IOptionsMonitor{T}"/> consumers
/// inject for hot-reloadable config.
///
/// Replaces the earlier pattern (one-shot <c>AddOptions().Configure()</c>
/// callback at host build time) with a service that re-reads the
/// systemoptions table on demand and fires <see cref="OnChange"/> when
/// <see cref="Reload"/> or <see cref="SaveAsync"/> is called.
///
/// <para>The on-disk side is the systemoptions SQLite table seeded by
/// <see cref="DbStartup"/> on first start. <c>SaveAsync</c> upserts each
/// known field; <c>Reload</c> re-parses without writing.</para>
/// </summary>
public sealed class SystemOptionsStore : IOptionsMonitor<SystemOptions>
{
    private readonly ILogger<SystemOptionsStore> logger;
    private SystemOptions current;
    private readonly List<Action<SystemOptions, string?>> listeners = new();
    private readonly object listenersLock = new();

    public SystemOptionsStore(ILogger<SystemOptionsStore> logger)
    {
        this.logger = logger;
        current = LoadSync();
    }

    public SystemOptions CurrentValue => current;
    public SystemOptions Get(string? name) => current;

    public IDisposable? OnChange(Action<SystemOptions, string?> listener)
    {
        lock (listenersLock) listeners.Add(listener);
        return new Subscription(this, listener);
    }

    /// <summary>Re-read the systemoptions table and fire OnChange.
    /// Used after an out-of-band edit (test fixtures) or when saving
    /// via <see cref="SaveAsync"/>.</summary>
    public void Reload()
    {
        var fresh = LoadSync();
        current = fresh;
        Action<SystemOptions, string?>[] snap;
        lock (listenersLock) snap = listeners.ToArray();
        foreach (var l in snap)
        {
            try { l(fresh, null); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SystemOptions OnChange listener threw");
            }
        }
    }

    /// <summary>Persist the supplied <paramref name="options"/> to the
    /// systemoptions table and fire OnChange. Hot-reloadable consumers
    /// (bearer services, transmission audit, …) react on the next tick.
    /// Restart-required consumers (Kestrel listener port, MQTT broker
    /// listener port) ignore the change until next start.</summary>
    public async Task SaveAsync(SystemOptions options)
    {
        var connection = DbInfo.GetAsyncConnection();
        var existing = (await connection.QueryAsync<DbSystemOption>("select * from systemoptions;"))
            .Select(r => r.Option)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await Upsert(connection, existing, nameof(options.NodeHost), options.NodeHost);
        await Upsert(connection, existing, nameof(options.AgwPort), options.AgwPort.ToString());
        await Upsert(connection, existing, nameof(options.NodeBearer), options.NodeBearer);
        await Upsert(connection, existing, nameof(options.RhpPort), options.RhpPort.ToString());
        await Upsert(connection, existing, nameof(options.RhpUser), options.RhpUser);
        await Upsert(connection, existing, nameof(options.RhpPass), options.RhpPass);
        await Upsert(connection, existing, nameof(options.DefaultBearerPort), options.DefaultBearerPort.ToString());
        await Upsert(connection, existing, nameof(options.Callsign), options.Callsign);
        await Upsert(connection, existing, nameof(options.MqttPort), options.MqttPort.ToString());
        await Upsert(connection, existing, nameof(options.UdpListenPort), options.UdpListenPort.ToString());
        await Upsert(connection, existing, nameof(options.AuthRequired), options.AuthRequired.ToString());
        await Upsert(connection, existing, nameof(options.UpdateCheckEnabled), options.UpdateCheckEnabled.ToString());
        await Upsert(connection, existing, nameof(options.RoutingAlgorithm), options.RoutingAlgorithm);
        await Upsert(connection, existing, nameof(options.ProbingEnabled), options.ProbingEnabled.ToString());
        await Upsert(connection, existing, nameof(options.ProbeIntervalHours), options.ProbeIntervalHours.ToString());
        await Upsert(connection, existing, nameof(options.FragmentThresholdBytes), options.FragmentThresholdBytes.ToString());
        await Upsert(connection, existing, nameof(options.FragmentReassemblyTimeoutSeconds), options.FragmentReassemblyTimeoutSeconds.ToString());
        await Upsert(connection, existing, nameof(options.OpportunisticPollEnabled), options.OpportunisticPollEnabled.ToString());
        await Upsert(connection, existing, nameof(options.ScheduledPollEnabled), options.ScheduledPollEnabled.ToString());
        await Upsert(connection, existing, nameof(options.PollIntervalHours), options.PollIntervalHours.ToString());
        await Upsert(connection, existing, nameof(options.DiscoveryAirtimeBudgetSecondsPerHour), options.DiscoveryAirtimeBudgetSecondsPerHour.ToString());
        await Upsert(connection, existing, nameof(options.ProbeStrategy), options.ProbeStrategy.ToString());
        await Upsert(connection, existing, nameof(options.ProbeOvernightStartHour), options.ProbeOvernightStartHour.ToString());
        await Upsert(connection, existing, nameof(options.ProbeOvernightEndHour), options.ProbeOvernightEndHour.ToString());
        await Upsert(connection, existing, nameof(options.ProbeQuietWindowSeconds), options.ProbeQuietWindowSeconds.ToString());
        await Upsert(connection, existing, nameof(options.HeartbeatEnabled), options.HeartbeatEnabled.ToString());
        await Upsert(connection, existing, nameof(options.HeartbeatIntervalSeconds), options.HeartbeatIntervalSeconds.ToString());
        await Upsert(connection, existing, nameof(options.AutoDiscoverViaNodeCall), options.AutoDiscoverViaNodeCall.ToString());
        await Upsert(connection, existing, nameof(options.NodePromptApplicationCommand), options.NodePromptApplicationCommand);
        await Upsert(connection, existing, nameof(options.TransmissionAuditEnabled), options.TransmissionAuditEnabled.ToString());
        await Upsert(connection, existing, nameof(options.TransmissionAuditRetentionDays), options.TransmissionAuditRetentionDays.ToString());
        await Upsert(connection, existing, nameof(options.TransmissionAuditMqttPublish), options.TransmissionAuditMqttPublish.ToString());
        await Upsert(connection, existing, nameof(options.TxEnabled), options.TxEnabled.ToString());
        await Upsert(connection, existing, nameof(options.MeshCoreEnabled), options.MeshCoreEnabled.ToString());
        await Upsert(connection, existing, nameof(options.MeshCorePort), options.MeshCorePort);
        await Upsert(connection, existing, nameof(options.MeshCoreRegion), options.MeshCoreRegion);
        await Upsert(connection, existing, nameof(options.MeshCoreTxPowerDbm), options.MeshCoreTxPowerDbm.ToString());
        await Upsert(connection, existing, nameof(options.MeshCoreChannelIndex), options.MeshCoreChannelIndex.ToString());
        await Upsert(connection, existing, nameof(options.MeshCoreChannelName), options.MeshCoreChannelName);
        await Upsert(connection, existing, nameof(options.MeshCoreChannelPsk), options.MeshCoreChannelPsk);
        await Upsert(connection, existing, nameof(options.MeshCoreNodeName), options.MeshCoreNodeName);
        await Upsert(connection, existing, nameof(options.MeshCoreAirtimeBudgetSecondsPerHour), options.MeshCoreAirtimeBudgetSecondsPerHour.ToString(CultureInfo.InvariantCulture));
        await Upsert(connection, existing, nameof(options.MeshCoreCompress), options.MeshCoreCompress.ToString());
        await Upsert(connection, existing, nameof(options.MeshCoreCongestionBackoffFraction), options.MeshCoreCongestionBackoffFraction.ToString(CultureInfo.InvariantCulture));
        await Upsert(connection, existing, nameof(options.MeshCoreLbtGuardMs), options.MeshCoreLbtGuardMs.ToString());
        await Upsert(connection, existing, nameof(options.MeshCoreReliableDelivery), options.MeshCoreReliableDelivery.ToString());

        Reload();
    }

    private static async Task Upsert(SQLiteAsyncConnection connection, HashSet<string> existing, string field, string value)
    {
        if (existing.Contains(field))
        {
            await connection.ExecuteAsync("update systemoptions set value=? where option=?", value, field);
        }
        else
        {
            await connection.InsertAsync(new DbSystemOption { Option = field, Value = value });
            existing.Add(field);
        }
    }

    private static SystemOptions LoadSync()
    {
        var connection = DbInfo.GetConnection();
        // Defensive: tests sometimes spin this up before DbStartup runs.
        // CreateTable is idempotent.
        connection.CreateTable<DbSystemOption>();
        var rows = connection.Query<DbSystemOption>("select * from systemoptions;")
            .ToDictionary(r => r.Option, r => r.Value, StringComparer.OrdinalIgnoreCase);
        return Parse(rows);
    }

    private static SystemOptions Parse(Dictionary<string, string> r)
    {
        return new SystemOptions
        {
            NodeHost = TryGet(r, nameof(SystemOptions.NodeHost), "localhost"),
            AgwPort = TryGetInt(r, nameof(SystemOptions.AgwPort), 8000),
            NodeBearer = TryGet(r, nameof(SystemOptions.NodeBearer), "agw").Trim().ToLowerInvariant(),
            RhpPort = TryGetInt(r, nameof(SystemOptions.RhpPort), 9000),
            RhpUser = TryGet(r, nameof(SystemOptions.RhpUser), ""),
            RhpPass = TryGet(r, nameof(SystemOptions.RhpPass), ""),
            DefaultBearerPort = TryGetInt(r, nameof(SystemOptions.DefaultBearerPort), 0),
            Callsign = TryGet(r, nameof(SystemOptions.Callsign), DbStartup.PlaceholderCallsign),
            MqttPort = TryGetInt(r, nameof(SystemOptions.MqttPort), 1883),
            UdpListenPort = TryGetInt(r, nameof(SystemOptions.UdpListenPort), 0),
            AuthRequired = TryGetBool(r, nameof(SystemOptions.AuthRequired), false),
            UpdateCheckEnabled = TryGetBool(r, nameof(SystemOptions.UpdateCheckEnabled), true),
            RoutingAlgorithm = TryGet(r, nameof(SystemOptions.RoutingAlgorithm), "passive-flood"),
            ProbingEnabled = TryGetBool(r, nameof(SystemOptions.ProbingEnabled), false),
            ProbeIntervalHours = TryGetInt(r, nameof(SystemOptions.ProbeIntervalHours), 24, min: 1),
            FragmentThresholdBytes = TryGetInt(r, nameof(SystemOptions.FragmentThresholdBytes), 4096, min: 0),
            FragmentReassemblyTimeoutSeconds = TryGetInt(r, nameof(SystemOptions.FragmentReassemblyTimeoutSeconds), 7 * 24 * 3600, min: 1),
            OpportunisticPollEnabled = TryGetBool(r, nameof(SystemOptions.OpportunisticPollEnabled), true),
            ScheduledPollEnabled = TryGetBool(r, nameof(SystemOptions.ScheduledPollEnabled), false),
            PollIntervalHours = TryGetInt(r, nameof(SystemOptions.PollIntervalHours), 6, min: 1),
            DiscoveryAirtimeBudgetSecondsPerHour = TryGetInt(r, nameof(SystemOptions.DiscoveryAirtimeBudgetSecondsPerHour), 0, min: 0),
            ProbeStrategy = r.TryGetValue(nameof(SystemOptions.ProbeStrategy), out var ps)
                && Enum.TryParse<ProbeStrategy>(ps, ignoreCase: true, out var psParsed)
                ? psParsed
                : ProbeStrategy.FixedInterval,
            ProbeOvernightStartHour = TryGetInt(r, nameof(SystemOptions.ProbeOvernightStartHour), 2, min: 0, max: 23),
            ProbeOvernightEndHour = TryGetInt(r, nameof(SystemOptions.ProbeOvernightEndHour), 6, min: 0, max: 23),
            ProbeQuietWindowSeconds = TryGetInt(r, nameof(SystemOptions.ProbeQuietWindowSeconds), 300, min: 1),
            HeartbeatEnabled = TryGetBool(r, nameof(SystemOptions.HeartbeatEnabled), true),
            HeartbeatIntervalSeconds = TryGetInt(r, nameof(SystemOptions.HeartbeatIntervalSeconds), 60, min: 10),
            AutoDiscoverViaNodeCall = TryGetBool(r, nameof(SystemOptions.AutoDiscoverViaNodeCall), false),
            NodePromptApplicationCommand = TryGet(r, nameof(SystemOptions.NodePromptApplicationCommand), "DAPPS"),
            TransmissionAuditEnabled = TryGetBool(r, nameof(SystemOptions.TransmissionAuditEnabled), true),
            TransmissionAuditRetentionDays = TryGetInt(r, nameof(SystemOptions.TransmissionAuditRetentionDays), 90, min: 0),
            TransmissionAuditMqttPublish = TryGetBool(r, nameof(SystemOptions.TransmissionAuditMqttPublish), false),
            TxEnabled = TryGetBool(r, nameof(SystemOptions.TxEnabled), true),
            MeshCoreEnabled = TryGetBool(r, nameof(SystemOptions.MeshCoreEnabled), false),
            MeshCorePort = TryGet(r, nameof(SystemOptions.MeshCorePort), "/dev/ttyUSB0"),
            MeshCoreRegion = TryGet(r, nameof(SystemOptions.MeshCoreRegion), "uk-test"),
            MeshCoreTxPowerDbm = TryGetInt(r, nameof(SystemOptions.MeshCoreTxPowerDbm), 8, min: 0, max: 30),
            MeshCoreChannelIndex = TryGetInt(r, nameof(SystemOptions.MeshCoreChannelIndex), 1, min: 0, max: 255),
            MeshCoreChannelName = TryGet(r, nameof(SystemOptions.MeshCoreChannelName), "dapps"),
            MeshCoreChannelPsk = TryGet(r, nameof(SystemOptions.MeshCoreChannelPsk), "dapps-default-channel"),
            MeshCoreNodeName = TryGet(r, nameof(SystemOptions.MeshCoreNodeName), "DAPPS"),
            MeshCoreAirtimeBudgetSecondsPerHour = TryGetDouble(r, nameof(SystemOptions.MeshCoreAirtimeBudgetSecondsPerHour), 30, min: 0),
            MeshCoreCompress = TryGetBool(r, nameof(SystemOptions.MeshCoreCompress), true),
            MeshCoreCongestionBackoffFraction = TryGetDouble(r, nameof(SystemOptions.MeshCoreCongestionBackoffFraction), 0.5, min: 0, max: 1),
            MeshCoreLbtGuardMs = TryGetInt(r, nameof(SystemOptions.MeshCoreLbtGuardMs), 400, min: 0),
            MeshCoreReliableDelivery = TryGetBool(r, nameof(SystemOptions.MeshCoreReliableDelivery), true),
        };
    }

    private static string TryGet(Dictionary<string, string> r, string key, string fallback)
        => r.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    private static int TryGetInt(Dictionary<string, string> r, string key, int fallback, int? min = null, int? max = null)
    {
        if (r.TryGetValue(key, out var s) && int.TryParse(s, out var v))
        {
            if (min is { } lo && v < lo) return fallback;
            if (max is { } hi && v > hi) return fallback;
            return v;
        }
        return fallback;
    }

    private static bool TryGetBool(Dictionary<string, string> r, string key, bool fallback)
        => r.TryGetValue(key, out var s) && bool.TryParse(s, out var v) ? v : fallback;

    private static double TryGetDouble(Dictionary<string, string> r, string key, double fallback, double? min = null, double? max = null)
    {
        if (r.TryGetValue(key, out var s)
            && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
        {
            if (min is { } lo && v < lo) return fallback;
            if (max is { } hi && v > hi) return fallback;
            return v;
        }
        return fallback;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly SystemOptionsStore store;
        private readonly Action<SystemOptions, string?> listener;
        public Subscription(SystemOptionsStore store, Action<SystemOptions, string?> listener)
        {
            this.store = store;
            this.listener = listener;
        }
        public void Dispose()
        {
            lock (store.listenersLock) store.listeners.Remove(listener);
        }
    }
}
