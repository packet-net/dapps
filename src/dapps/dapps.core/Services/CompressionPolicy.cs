using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Whether payloads to a peer may go compressed on a DAPPSv1 session:
/// the neighbour's own setting when it has one
/// (<see cref="DbNeighbour.CompressionEnabled"/>), otherwise the
/// system-wide <see cref="SystemOptions.CompressionEnabled"/>. Only
/// governs what this node sends; compressed payloads from anyone are
/// always accepted. "Compressed" still means only when it saves bytes,
/// see <see cref="dapps.client.Compression.PayloadCompression"/>.
/// </summary>
public sealed class CompressionPolicy(Database database, IOptionsMonitor<SystemOptions> options)
{
    public async Task<bool> ShouldCompressToAsync(string peerCallsign, CancellationToken ct)
    {
        var neighbours = await database.GetNeighbours();
        var row = neighbours.FirstOrDefault(n => string.Equals(n.Callsign, peerCallsign, StringComparison.OrdinalIgnoreCase));
        return row?.CompressionEnabled ?? options.CurrentValue.CompressionEnabled;
    }
}
