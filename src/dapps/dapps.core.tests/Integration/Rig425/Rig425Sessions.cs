using System.Text.RegularExpressions;

namespace dapps.core.tests.Integration.Rig425;

/// <summary>
/// One session an application on LOCAL holds, whichever API carries it.
/// <see cref="Text"/> is everything received on it so far.
/// </summary>
public abstract class RigSession
{
    public required string Label { get; init; }

    /// <summary>Null while it is up or opening; otherwise why it is not (refused, closed).</summary>
    public string? Error { get; protected set; }

    public abstract bool Connected { get; }
    public abstract bool Closed { get; }
    public abstract string Text { get; }
    public abstract Task OpenAsync(CancellationToken ct);
    public abstract Task SendAsync(string text, CancellationToken ct);
    public abstract Task CloseAsync(CancellationToken ct);

    /// <summary>Type a line and give the node or app a moment to answer.</summary>
    public async Task TypeAsync(string line, CancellationToken ct, int waitMs = 3000)
    {
        await SendAsync(line + "\r", ct);
        await Task.Delay(waitMs, ct);
    }

    /// <summary>Every "APP x sees y" greeting this session got, as "y>x".</summary>
    public IReadOnlyList<string> Sightings() =>
        [.. Regex.Matches(Text, @"APP (\S+) sees (\S+)").Select(m => $"{m.Groups[2].Value}>{m.Groups[1].Value}")];

    public bool Echoed(string line) => Text.Contains(": " + line, StringComparison.Ordinal);

    public string Summary()
    {
        var state = Error is not null ? $"error: {Error}" : Closed ? "closed" : Connected ? "up" : "not up";
        return $"{Label} [{state}]";
    }
}

/// <summary>An AGW session, identified by the node as (port byte, from, to).</summary>
public sealed class AgwSession(AgwRig agw, byte port, string from, string to) : RigSession
{
    private int start;
    public AgwRig Socket => agw;
    public byte Port => port;
    public string From => from;
    public string To => to;

    private IEnumerable<AgwEvent> Mine => agw.Events.Skip(start).Where(e => e.Port == port &&
        ((e.From == from && e.To == to) || (e.From == to && e.To == from)));

    public int ConnectReplies => Mine.Count(e => e.Kind == 'C');
    public override bool Connected => ConnectReplies > 0;
    public override bool Closed => Mine.Any(e => e.Kind == 'd');
    public override string Text => string.Concat(Mine.Where(e => e.Kind == 'D').Select(e => e.Text));

    public override async Task OpenAsync(CancellationToken ct)
    {
        start = agw.Events.Count;
        var before = ConnectReplies;
        await agw.ConnectAsync(port, from, to, ct);
        if (!await RigLog.WaitAsync(() => ConnectReplies > before || Closed, TimeSpan.FromSeconds(20), ct))
            Error = "no reply to 'C' in 20 s";
        else if (Closed && !Connected)
            Error = "'d' instead of 'C'";
    }

    /// <summary>Send a second 'C' on the same key, as a second session for the same pair would.</summary>
    public Task ReconnectAsync(CancellationToken ct) => agw.ConnectAsync(port, from, to, ct);

    public override Task SendAsync(string text, CancellationToken ct) => agw.SendAsync(port, from, to, text, ct);

    public override Task CloseAsync(CancellationToken ct) => agw.DisconnectAsync(port, from, to, ct);
}

/// <summary>A LinBPQ RHP stream: it starts at LOCAL's node prompt.</summary>
public sealed class BpqRhpSession(BpqRhpRig rhp, string local) : RigSession
{
    public int Handle { get; private set; }
    private int start;

    public override bool Connected => Handle > 0 && rhp.Received(Handle, start).Contains("Connected to RHP Server", StringComparison.Ordinal);
    public override bool Closed => Handle > 0 && rhp.SawClose(Handle, start);
    public override string Text => Handle > 0 ? rhp.Received(Handle, start) : "";

    public override async Task OpenAsync(CancellationToken ct)
    {
        start = rhp.Messages.Count;
        var (h, reply) = await rhp.OpenAsync(local, "SWITCH", 1, ct);
        Handle = h;
        if (h <= 0) Error = reply;
        else await RigLog.WaitAsync(() => Connected, TimeSpan.FromSeconds(10), ct);
    }

    public override Task SendAsync(string text, CancellationToken ct) => rhp.SendAsync(Handle, text, ct);

    public override Task CloseAsync(CancellationToken ct) => rhp.CloseAsync(Handle, ct);
}

/// <summary>An RHPv2 stream on XRouter or pdn.</summary>
public sealed class RhpSession(RhpRig rhp, string pfam, string? port, string local, string remote) : RigSession
{
    public int Handle { get; private set; }

    public override bool Connected => Handle > 0;
    public override bool Closed => Handle > 0 && rhp.SawClose(Handle);
    public override string Text => Handle > 0 ? rhp.Received(Handle) : "";

    public override async Task OpenAsync(CancellationToken ct)
    {
        var (h, err) = await rhp.OpenAsync(pfam, port, local, remote, ct);
        Handle = h;
        Error = err;
        if (h > 0) await Task.Delay(1000, ct);
    }

    public override async Task SendAsync(string text, CancellationToken ct) => await rhp.SendAsync(Handle, text, ct);

    public override Task CloseAsync(CancellationToken ct) => rhp.CloseAsync(Handle, ct);
}
