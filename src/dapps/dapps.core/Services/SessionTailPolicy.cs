using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// How long to hold a DAPPSv1 session with a peer open after its
/// traffic (#187 proposal 9): the neighbour's own setting when it has one
/// (<see cref="DbNeighbour.SessionTailSeconds"/>), otherwise the
/// system-wide <see cref="SystemOptions.SessionTailSeconds"/>. 0 means
/// don't hold. Both ends apply their own setting and the session is held
/// for the lower of the two.
/// </summary>
public sealed class SessionTailPolicy(Database database, IOptionsMonitor<SystemOptions> options)
{
    /// <summary>Many nodes drop a circuit that has been idle for 10 minutes.</summary>
    public const int MaxSeconds = 600;

    public async Task<int> TailSecondsForAsync(string peerCallsign, CancellationToken ct)
    {
        var neighbours = await database.GetNeighbours();
        var row = neighbours.FirstOrDefault(n => string.Equals(n.Callsign, peerCallsign, StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(row?.SessionTailSeconds ?? options.CurrentValue.SessionTailSeconds, 0, MaxSeconds);
    }
}
