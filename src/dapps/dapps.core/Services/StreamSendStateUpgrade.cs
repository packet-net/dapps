using dapps.core.Models;
using SQLite;

namespace dapps.core.Services;

/// <summary>
/// Merges stream send counters saved under different spellings of one
/// destination (#390).
///
/// Up to 0.53 the counter was keyed on the destination as written, so
/// <c>app@G0DEST</c>, <c>app@g0dest</c> and <c>app@G0DEST-0</c> each had
/// their own counter for the same stream. The destination is now keyed on
/// its station form (<see cref="DestinationParser.StationKey"/>); our own
/// callsign is left as it was (see <see cref="DbStreamSendState.Key"/>).
/// Rows that differ only by the destination's spelling become one row,
/// carrying on from the
/// highest seq any of them reached, so no seq the receiver has already
/// seen is minted again. Runs at every start; once merged there is
/// nothing left to do.
/// </summary>
public static class StreamSendStateUpgrade
{
    public static void MergeSpellings(SQLiteConnection db, ILogger? logger)
    {
        var rows = db.Query<DbStreamSendState>("select * from streamsendstate");
        var groups = rows
            .GroupBy(r => DbStreamSendState.MakeKey(r.LocalCallsign, r.RemoteCallsign, r.StreamId))
            .Where(g => g.Count() > 1 || g.Single().Key != g.Key)
            .ToList();
        if (groups.Count == 0) return;

        db.RunInTransaction(() =>
        {
            foreach (var g in groups)
            {
                foreach (var r in g) db.Delete<DbStreamSendState>(r.Key);
                var first = g.First();
                db.Insert(new DbStreamSendState
                {
                    Key = g.Key,
                    LocalCallsign = first.LocalCallsign,
                    RemoteCallsign = DestinationParser.StationKey(first.RemoteCallsign),
                    StreamId = first.StreamId,
                    NextSeq = g.Max(r => r.NextSeq),
                    UpdatedAt = g.Max(r => r.UpdatedAt),
                });
            }
        });
        logger?.LogInformation("Merged stream send counters for {0} stream(s) written with different callsign spellings", groups.Count);
    }
}
