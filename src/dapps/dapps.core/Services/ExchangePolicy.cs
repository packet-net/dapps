using dapps.client.Backhaul;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// This node's settings for a DAPPSv1 session with a neighbour, stated to
/// it in our <c>exchange</c> line and applied to what we send it: how
/// long to hold a quiet link (<see cref="SessionTailPolicy"/>), whether
/// to compress (<see cref="CompressionPolicy"/>), and the largest message
/// we take (<see cref="SystemOptions.MaxMessageBytes"/>).
/// </summary>
public sealed class ExchangePolicy(CompressionPolicy compression, SessionTailPolicy tail, IOptionsMonitor<SystemOptions> options)
{
    public async Task<ExchangeSettings> ForPeerAsync(string peerCallsign, CancellationToken ct)
    {
        var max = options.CurrentValue.MaxMessageBytes;
        return new ExchangeSettings(
            HoldSeconds: await tail.TailSecondsForAsync(peerCallsign, ct),
            Compress: await compression.ShouldCompressToAsync(peerCallsign, ct),
            MaxBytes: max > 0 ? max : null);
    }
}
