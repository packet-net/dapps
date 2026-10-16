using System.Text.Json;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.Services;

/// <summary>
/// Single recording surface for the transmission audit log. Every
/// outbound transmission (beacon, solicit, probe, forward, poll, ack,
/// heartbeat) calls <see cref="RecordAsync"/> with what it just did
/// and why; the service writes a row to the <c>transmissions</c>
/// table and (optionally) publishes the row to MQTT topic
/// <c>dapps/audit/tx</c>.
///
/// Failures here are swallowed by design - if writing the audit row
/// breaks for any reason (disk full, MQTT broker not yet up at start),
/// we still want the actual transmission to count as having happened
/// from the caller's perspective. We log the audit failure and move on.
/// </summary>
public sealed class TransmissionAuditService(
    ILogger<TransmissionAuditService> logger,
    IOptionsMonitor<SystemOptions> options,
    MqttBrokerService mqttBroker,
    TimeProvider timeProvider)
{
    private const string MqttTopic = "dapps/audit/tx";

    /// <summary>
    /// Record one transmission. The <paramref name="reason"/> is the
    /// "why" - keep it under ~80 chars so the dashboard table stays
    /// readable. <paramref name="kind"/> is one of the values listed
    /// on <see cref="DbTransmission.Kind"/>.
    /// </summary>
    public async Task RecordAsync(
        string kind,
        string bearer,
        string reason,
        bool success,
        string targetCallsign = "",
        string channelKey = "",
        string messageId = "",
        int bytes = 0,
        int durationMs = 0,
        string errorTag = "")
    {
        if (!options.CurrentValue.TransmissionAuditEnabled) return;

        var row = new DbTransmission
        {
            At = timeProvider.GetUtcNow().UtcDateTime,
            Kind = kind,
            Bearer = bearer,
            ChannelKey = channelKey,
            TargetCallsign = targetCallsign,
            MessageId = messageId,
            Bytes = bytes,
            DurationMs = durationMs,
            Success = success,
            Reason = reason,
            ErrorTag = errorTag,
        };

        try
        {
            var connection = DbInfo.GetAsyncConnection();
            await connection.InsertAsync(row);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "transmission audit: insert failed (kind={0} target={1})", kind, targetCallsign);
            // Don't let the audit failure abort whatever the caller
            // was doing; they've already transmitted.
        }

        if (options.CurrentValue.TransmissionAuditMqttPublish)
        {
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(row);
                await mqttBroker.PublishAsync(MqttTopic, json);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "transmission audit: MQTT publish failed");
            }
        }
    }

    /// <summary>
    /// Pull the recent transmission rows for dashboard / REST
    /// callers. Newest first; capped at <paramref name="limit"/>.
    /// Optional filter by kind (one or more) and target callsign.
    /// </summary>
    public async Task<IReadOnlyList<DbTransmission>> ListRecentAsync(
        int limit = 200,
        IReadOnlyCollection<string>? kinds = null,
        string? targetCallsign = null,
        bool? successOnly = null)
    {
        if (limit <= 0) limit = 200;
        var clauses = new List<string>();
        var args = new List<object>();
        if (kinds is { Count: > 0 })
        {
            // Inline the IN list. SQLite's parameter binding doesn't do
            // expansion, and the kind values are a small constrained set
            // so building the SQL is safe (still parameterised per slot).
            var placeholders = string.Join(",", kinds.Select((_, i) => "?"));
            clauses.Add($"Kind in ({placeholders})");
            args.AddRange(kinds);
        }
        if (!string.IsNullOrEmpty(targetCallsign))
        {
            clauses.Add("TargetCallsign = ?");
            args.Add(targetCallsign);
        }
        if (successOnly is { } so)
        {
            clauses.Add("Success = ?");
            args.Add(so ? 1 : 0);
        }
        var where = clauses.Count == 0 ? "" : "where " + string.Join(" and ", clauses);
        var sql = $"select * from transmissions {where} order by At desc limit ?";
        args.Add(limit);

        var connection = DbInfo.GetAsyncConnection();
        return await connection.QueryAsync<DbTransmission>(sql, args.ToArray());
    }

    /// <summary>
    /// Delete rows older than the configured retention window.
    /// Returns the number of rows removed. Called by the retention
    /// sweeper on its tick; safe to call ad-hoc from REST if an
    /// operator wants to force a sweep.
    /// </summary>
    public async Task<int> SweepOldRowsAsync()
    {
        var days = options.CurrentValue.TransmissionAuditRetentionDays;
        if (days <= 0) return 0;
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
        var connection = DbInfo.GetAsyncConnection();
        return await connection.ExecuteAsync("delete from transmissions where At < ?", cutoff);
    }
}
