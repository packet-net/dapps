namespace dapps.client.Transport.Agw;

/// <summary>
/// How one node numbers its radio ports, for turning the number an
/// operator sees on the node (and types into DAPPS) into the port byte
/// the node's AGW connection uses, and back (#233, #370).
///
/// Most AGW hosts number their ports 1, 2, 3 in the order their AGW port
/// list gives them, and their AGW byte counts the same ports from 0: BPQ
/// (its <c>PORTNUM</c>, when the ports are numbered 1..N in config order),
/// Dire Wolf and AGWPE (the <c>PortN</c> in the list: channel 0 is Port 1).
/// That is <see cref="Plain"/>: byte = number - 1.
///
/// XRouter's ports are its <c>PORT=</c> numbers, which may have gaps, and
/// its AGW counts them from 0 in order of those numbers, lowest first,
/// whatever order XROUTER.CFG writes them in. Its AGW never says which
/// numbers they are (the list says <c>Port1</c>, <c>Port2</c>, ... and the
/// monitor shows the same position), so DAPPS reads them from the node's
/// RHPv2 port: <see cref="Xrouter"/>.
///
/// RHPv2 needs no map: XRouter and pdn both take a port by the number
/// the operator sees.
/// </summary>
public sealed class NodePortMap
{
    private readonly IReadOnlyList<int>? numbers;
    private readonly int? listed;

    private NodePortMap(IReadOnlyList<int>? numbers, int? listed)
    {
        this.numbers = numbers;
        this.listed = listed;
    }

    /// <summary>Byte = number - 1. <paramref name="listedPorts"/> is how
    /// many ports the node's AGW port list names, when it gave one: a
    /// number past them is refused rather than sent (BPQ sends an unknown
    /// port's frames out on another port).</summary>
    public static NodePortMap Plain(int? listedPorts = null) => new(null, listedPorts is > 0 ? listedPorts : null);

    /// <summary>XRouter's ports, by <c>PORT=</c> number, in its AGW order
    /// (lowest first).</summary>
    public static NodePortMap Xrouter(IReadOnlyList<int> numbersInAgwOrder) =>
        new(numbersInAgwOrder.ToArray(), numbersInAgwOrder.Count);

    /// <summary>True when the node is XRouter and the numbers came from its
    /// RHPv2 port, so its AGW ports are matched to its RHPv2 ones.</summary>
    public bool IsXrouter => numbers is not null;

    /// <summary>The node's port numbers in AGW order, when known (XRouter,
    /// or a plain host's listed ports).</summary>
    public IReadOnlyList<int>? Numbers =>
        numbers ?? (listed is { } n ? Enumerable.Range(1, n).ToArray() : null);

    /// <summary>The AGW port byte for the node's port
    /// <paramref name="number"/>, or null when the node has no such port.</summary>
    public byte? ToAgwPort(int number)
    {
        if (numbers is not null)
        {
            for (var i = 0; i < numbers.Count; i++)
            {
                if (numbers[i] == number) return (byte)i;
            }
            return null;
        }
        if (number < 1 || number > AgwFrameTransport.MaxPort + 1) return null;
        if (listed is { } n && number > n) return null;
        return (byte)(number - 1);
    }

    /// <summary>The node's port number for AGW port byte
    /// <paramref name="port"/>, or null when it has no such port.</summary>
    public int? FromAgwPort(byte port)
    {
        if (numbers is not null) return port < numbers.Count ? numbers[port] : null;
        if (listed is { } n && port >= n) return null;
        return port + 1;
    }

    /// <summary>As <see cref="FromAgwPort"/>, but for a plain host never
    /// refused for being past the port list: for converting a stored AGW
    /// byte, which must keep naming the byte it named before.</summary>
    public int? FromAgwPortUnbounded(byte port) =>
        numbers is not null ? (port < numbers.Count ? numbers[port] : null) : port + 1;

    public override string ToString() =>
        numbers is not null ? $"XRouter ports [{string.Join(",", numbers)}]"
        : listed is { } n ? $"ports 1 to {n}" : "ports counted from 1";
}

/// <summary>
/// The port numbering of the node behind an AGW connection, worked out
/// once per connection (<see cref="NodePortMap"/>).
/// </summary>
public interface INodePortMaps
{
    /// <summary>The map for <paramref name="link"/> if it is known now,
    /// without waiting: for the link's reader. Null while an XRouter's
    /// numbers are still being read (or could not be).</summary>
    NodePortMap? TryGet(AgwLink link);

    /// <summary>The map for <paramref name="link"/>, worked out if need
    /// be. Null when the node is XRouter and its RHPv2 port could not be
    /// read: nothing should then be sent by number on that link.</summary>
    Task<NodePortMap?> GetAsync(AgwLink link, CancellationToken ct);
}
