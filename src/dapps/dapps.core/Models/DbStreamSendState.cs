using dapps.core.Services;
using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Sender-side counter for opt-in message ordering. One row per
/// (LocalCallsign, RemoteCallsign, StreamId) - the daemon mints
/// monotonically increasing seq numbers from this row when stamping
/// outbound messages. Persisted so a reboot doesn't reset the counter
/// mid-stream and cause receiver-side dup drops or seq collisions.
///
/// StreamId is sender-scoped (per the protocol design): the receiver
/// keys its cursor on (originator-callsign, StreamId), so two senders
/// can pick the same StreamId without colliding.
/// </summary>
[Table("streamsendstate")]
public sealed class DbStreamSendState
{
    /// <summary>Composite key <c>{LocalCallsign}|{RemoteCallsign}|{StreamId}</c>,
    /// with the remote callsign in its station form (upper case, no "-0"),
    /// so every spelling of a destination shares one counter (#390). Our
    /// own callsign stays as configured: the receiver keys its cursor on
    /// the originator as sent, so a new spelling of ours starts a fresh
    /// cursor there and must start a fresh counter here. Built by
    /// <see cref="MakeKey"/> on insert.</summary>
    [PrimaryKey, NotNull]
    public string Key { get; set; } = "";

    public string LocalCallsign { get; set; } = "";
    public string RemoteCallsign { get; set; } = "";
    public string StreamId { get; set; } = "";

    /// <summary>The next seq to mint. After taking it, the daemon
    /// updates this row to <c>NextSeq + 1</c> in the same transaction
    /// as persisting the outbound message, so a crash between the
    /// two leaves the counter coherent with what's on disk.</summary>
    public uint NextSeq { get; set; } = 1;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string MakeKey(string localCallsign, string remoteCallsign, string streamId)
        => $"{localCallsign}|{DestinationParser.StationKey(remoteCallsign)}|{streamId}";
}
