using dapps.client.Tx;
using RhpV2.Client;

namespace dapps.client.Transport.Rhp;

/// <summary>
/// How a connected session's bytes go to an RHPv2 node, for our calls
/// and the peers' alike.
/// </summary>
public static class RhpSessionWrites
{
    /// <summary>
    /// The write callback for a session's <see cref="Agw.MultiplexedAgwSessionStream"/>
    /// on <paramref name="handle"/>. Each write goes as sends of at most
    /// <see cref="NodeSessionWrites.MaxBytes"/> (see there for why), one
    /// write's sends one after another with no other write's between them.
    /// RF-emitting: a send becomes AX.25 I-frames, so the TX gate is checked
    /// for the whole write first, and a session opened before TX stop goes
    /// silent the moment it is hit.
    /// </summary>
    /// <param name="handle">The session's handle (read at each write: the
    /// outbound open learns it only after the stream exists).</param>
    /// <param name="what">Names the session in a TX stop message.</param>
    public static Func<byte[], CancellationToken, Task> Writer(
        RhpClient rhp, Func<int> handle, IDappsTxGate gate, string what)
    {
        var writing = new SemaphoreSlim(1, 1);
        return async (data, ct) =>
        {
            await writing.WaitAsync(ct);
            try
            {
                if (!gate.TxAllowed)
                {
                    throw new TxStoppedException($"{what} {handle()}: {gate.BlockReason ?? "(no reason)"}");
                }
                foreach (var chunk in NodeSessionWrites.Split(data))
                {
                    await rhp.SendOnHandleAsync(handle(), chunk.ToArray(), ct);
                }
            }
            finally
            {
                writing.Release();
            }
        };
    }
}
