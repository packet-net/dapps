using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Every message this node has accepted from a peer, for delivery here
/// or to pass on, so the same message is never delivered or forwarded
/// twice. A sender that restarted, or lost our <c>ack</c> on a dropped
/// link, offers the message again; so can a second neighbour on another
/// path. The inbox (<see cref="Services.DatabaseAndMqttInbox"/>) drops
/// such repeats, and an <c>ihave</c> for one is answered <c>ack</c>
/// straight away, so its payload never goes over the air again.
///
/// The key is the message id together with its salt (<c>s=</c>) and
/// length: the id alone is only 28 bits of hash, and a node remembering
/// weeks of traffic would sometimes find a new message matching an old
/// one and drop it. Each fragment of a split message is its own entry.
///
/// A row is written as "being stored" (<see cref="Committed"/> false)
/// before the message is stored, and marked committed once it is, so a
/// node that dies in between leaves a row that doesn't count: the
/// sender's retry is taken, not answered "already got it". A crash can
/// cause a repeat, never a loss.
///
/// A row lives until the message would have expired anyway (its TTL at
/// receipt, plus an hour for clock and queue slack), and at most
/// <see cref="SystemOptions.ReceivedMemorySeconds"/>, which is also how
/// long a message with no TTL is remembered. After that no sender should
/// still be offering it.
///
/// Messages without a salt aren't remembered: a later message with the
/// same content would have the same key, and would be dropped.
/// </summary>
[Table("received")]
public class DbReceived
{
    [PrimaryKey]
    public string Key { get; set; } = "";

    public DateTime ReceivedAt { get; set; }

    [Indexed]
    public DateTime ExpiresAt { get; set; }

    /// <summary>The neighbour it first came from, for diagnostics.</summary>
    public string LinkSourceCallsign { get; set; } = "";

    /// <summary>False while the message is being stored; true once it is.</summary>
    public bool Committed { get; set; }

    public static string MakeKey(string id, long salt, int length) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{id}|{salt}|{length}");

    /// <summary>Slack on top of a message's TTL before its row is forgotten.</summary>
    public static readonly TimeSpan ExpirySlack = TimeSpan.FromHours(1);
}
