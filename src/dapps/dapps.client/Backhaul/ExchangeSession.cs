using System.Text;
using System.Threading.Channels;
using dapps.client.Compression;
using dapps.client.Transport;
using Microsoft.Extensions.Logging;

namespace dapps.client.Backhaul;

/// <summary>
/// One DAPPSv1 session with a neighbour, the same at both ends: the node
/// that dialled and the node that answered run this, and once each has
/// the other's <c>exchange</c> line either side sends whenever it has
/// something (docs/implement.md, "Sessions between DAPPS nodes").
///
/// <para>
/// Everything happens on one loop: it reads what the peer sent, answers
/// it, tops up what we have in flight from the forwarder's batches, and
/// writes all of that in one go before it waits again. A burst from the
/// peer is read in full before the answers go, so they share a write,
/// and on AX.25 a frame. That holds for a long message too: it can span
/// several of the peer's transmissions, and writing into those turns
/// had both ends' nodes streaming at once, which at the edge of range
/// made BPQ drop the link every minute or two.
/// </para>
///
/// <para>
/// A message of ours leaves the queue only when the peer acks it; one
/// still unanswered when the session ends goes back to the forwarder as
/// deferred, and the peer's duplicate memory makes sending it again safe.
/// When a session we dialled ends any way other than a <c>quit</c>, the
/// oldest unanswered message fails instead, so the neighbour gets a
/// cooldown and routing hears about it rather than the forwarder dialling
/// straight back into a link that can't carry it. A session at either end
/// that gets no answer to anything of ours for the answer timeout ends
/// the same way.
/// </para>
/// </summary>
public sealed class ExchangeSession
{
    /// <summary>Messages we have in flight at once, per direction.</summary>
    public const int DefaultWindow = 8;

    private const string Prompt = "DAPPSv1>";

    /// <summary>A payload bigger than this can't be meant; reading it
    /// would only tie the session up.</summary>
    private const int MaxWireBytes = 16 * 1024 * 1024;

    private const int MaxLineBytes = 8192;

    private static readonly string[] QuitCommands = ["q", "bye", "quit", "exit"];

    private readonly PumpedReadStream link;
    private readonly bool ownsLink;
    private readonly string peer;
    private readonly bool dialled;
    private readonly ExchangeSettings settings;
    private readonly IBackhaulInbox? inbox;
    private readonly ILogger logger;
    private readonly Channel<IBackhaulBatch> work = Channel.CreateUnbounded<IBackhaulBatch>();
    private readonly MemoryStream outgoing = new();

    /// <summary>Ours, sent and not yet answered, in the order they went.</summary>
    private readonly List<Outgoing> unanswered = [];

    /// <summary>The peer's offers we answered <c>send</c> to, by id: the
    /// payload's length and encoding come from here.</summary>
    private readonly Dictionary<string, IHaveOffer> accepted = new(StringComparer.Ordinal);

    /// <summary>Every session tag the peer has used on this link.</summary>
    private readonly HashSet<string> peerTags = new(StringComparer.Ordinal);

    private readonly List<DappsProtocolClient.GossipedRoute> gossiped = [];

    private IBackhaulBatch? current;
    private bool ownSent;
    private bool promptSeen;
    private bool awaitingRoutes;
    private bool plainOnly;
    private bool quitting;
    private bool peerQuit;
    private bool stuck;
    private bool done;
    private int noiseBytes;
    private int noiseSinceOwn;
    private bool heardDapps;
    private DateTimeOffset ownSentAt;
    private DateTimeOffset started;
    private DateTimeOffset lastHeard;
    private DateTimeOffset lastTraffic;
    private DateTimeOffset lastAnswered;
    private DateTimeOffset quitDeadline;

    /// <param name="stream">The connected link. Wrapped in a
    /// <see cref="PumpedReadStream"/> unless it already is one.</param>
    /// <param name="peer">The neighbour's callsign.</param>
    /// <param name="dialled">True for the node that made the call: it
    /// waits for the prompt and is the one that says <c>quit</c>. The
    /// answering node sends the prompt and takes commands until the
    /// caller's <c>exchange</c> arrives.</param>
    /// <param name="inbox">Where messages from the peer go. Null refuses
    /// them with <c>no</c>.</param>
    public ExchangeSession(Stream stream, string peer, bool dialled, ExchangeSettings settings, IBackhaulInbox? inbox, ILoggerFactory loggerFactory)
    {
        if (stream is PumpedReadStream pumped)
        {
            link = pumped;
        }
        else
        {
            link = new PumpedReadStream(stream);
            ownsLink = true;
        }
        this.peer = peer;
        this.dialled = dialled;
        this.settings = settings;
        this.inbox = inbox;
        logger = loggerFactory.CreateLogger<ExchangeSession>();
        OwnRules = new ExchangeRules(
            ExchangeRules.NewTag(), Math.Max(0, settings.HoldSeconds), Math.Max(0, settings.Inline), settings.MaxBytes,
            [.. PayloadCompression.Versions.Order()]);
    }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>How long a caller waits for the prompt or the peer's
    /// <c>exchange</c> before sending its own anyway: when two nodes
    /// dial each other at once, neither hears a prompt.</summary>
    public TimeSpan PromptWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a caller that sent its <c>exchange</c> without having heard
    /// a prompt waits for anything at all to come back before it hangs up:
    /// three prompt waits. In a crossed call the peer's rules come within
    /// about one. Silence that long means the call landed somewhere that
    /// will never answer: at the edge of range, on a link the far node
    /// reset and left attached to nothing (docs-internal/end-to-end-tests.md).
    /// Waiting there for the 3-minute inactivity timeout kept the link
    /// busy until the peer's own next call reset it, and the two nodes
    /// then reset each other's calls for 20 minutes.
    /// </summary>
    private TimeSpan SilentPeerWait => PromptWait * 3;

    /// <summary>Our rules went without a prompt, and nothing has come back.</summary>
    private bool WaitingOnSilence => ownSent && !promptSeen && !Established && noiseSinceOwn == 0 && !heardDapps;

    /// <summary>The shortest quiet spell before a caller ends the
    /// session, whatever the hold: the peer's first traffic can follow
    /// its <c>exchange</c> by a moment.</summary>
    public TimeSpan MinQuiet { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Longest a caller keeps a session up, however busy, so a
    /// link in steady use still picks up fresh routes and settings. The
    /// next message dials afresh.</summary>
    public TimeSpan MaxLength { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long to go without hearing anything from the peer
    /// before giving up on the link: the AX.25 T3 default. Longer once a
    /// hold has been agreed.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public int Window { get; init; } = DefaultWindow;

    /// <summary>For a caller: a connect script already read the prompt.</summary>
    public bool PromptConsumed { get; init; }

    /// <summary>
    /// For a caller: completes if the transport saw the peer's own call to
    /// us cross ours (<see cref="Transport.IDappsConnection.CrossedCall"/>).
    /// Neither end is answering then, so the caller sends its exchange at
    /// once instead of waiting out <see cref="PromptWait"/>.
    /// </summary>
    public Task? CrossedCall { get; init; }

    /// <summary>For a caller: pull the peer's routes before the exchange
    /// when the gossip gate says so.</summary>
    public IRouteGossipPort? RouteGossip { get; init; }

    /// <summary>For the answering node, before the exchange: the answer
    /// to a command such as <c>peers</c>, or null for one it doesn't know
    /// (answered <c>eh?</c>, then the session ends).</summary>
    public Func<string, CancellationToken, Task<string?>>? Commands { get; init; }

    /// <summary>Called once both <c>exchange</c> lines have crossed: the
    /// session can take work for the peer from now on.</summary>
    public Action<ExchangeSession>? Opened { get; init; }

    /// <summary>Called with the id of a payload that didn't hash to it.</summary>
    public Action<string>? HashMismatch { get; init; }

    public string Peer => peer;
    public ExchangeRules OwnRules { get; }
    public ExchangeRules? PeerRules { get; private set; }

    /// <summary>Both <c>exchange</c> lines have crossed.</summary>
    public bool Established { get; private set; }

    /// <summary>Messages from the peer handed to the inbox.</summary>
    public int Delivered { get; private set; }

    /// <summary>Why a caller's session ended before it was established;
    /// null otherwise.</summary>
    public string? Failure { get; private set; }

    /// <summary>
    /// Take a batch of work for the peer. Its messages go once the
    /// session is established, as the window allows. False once the
    /// session is ending; the messages then stay queued. A batch taken
    /// and not finished when the session ends has what's left of it
    /// deferred.
    /// </summary>
    public bool TryTake(IBackhaulBatch batch) => work.Writer.TryWrite(batch);

    /// <summary>
    /// Run the session until it ends: the caller's <c>quit</c> after a
    /// quiet spell, the peer hanging up, or the link going.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        started = lastHeard = lastTraffic = Now;
        try
        {
            if (!dialled)
            {
                // The prompt, and our rules straight after it.
                Queue(Prompt + "\n" + OwnRules.ToLine());
                ownSent = true;
            }
            else if (PromptConsumed)
            {
                await OnPromptAsync(ct);
            }

            while (true)
            {
                if (!done) await FillWindowAsync(ct);
                await FlushAsync(ct);
                if (done) break;
                if (await CheckTimersAsync()) continue;
                await WaitAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        {
            Failure ??= Established ? null : ex.Message;
            logger.LogInformation("Session with {0} ended: {1}", peer, ex.Message);
        }
        catch (Exception ex)
        {
            Failure ??= Established ? null : ex.Message;
            logger.LogWarning(ex, "Session with {0} failed", peer);
        }
        finally
        {
            End();
            await SettleUnfinishedAsync(ct.IsCancellationRequested);
            if (ownsLink) link.Dispose();
        }
    }

    /// <summary>Ends the session: nothing more is read, and no more work is taken.</summary>
    private void End()
    {
        done = true;
        work.Writer.TryComplete();
    }

    /// <summary>
    /// What's still ours when the session ends. Unanswered messages go
    /// back to the queue, except that a session we dialled which ended
    /// without a <c>quit</c> (the peer hung up, the link failed or went
    /// silent, the stream went out of step), or either end's session that
    /// got no answers for the answer timeout, fails its oldest one: the
    /// neighbour gets a cooldown, and routing learns of it. Not when
    /// <paramref name="deferAll"/>: we're shutting down, or the link went
    /// to a newer session. Work handed to the session and not started yet
    /// is deferred too, so every message handed out gets an outcome, a
    /// flood copy included.
    /// </summary>
    private async Task SettleUnfinishedAsync(bool deferAll)
    {
        var failOldest = Established && !deferAll && (stuck || dialled && !quitting && !peerQuit);
        foreach (var o in unanswered.ToList())
        {
            await CompleteAsync(o, failOldest
                ? BackhaulSendResult.Fail(stuck
                    ? $"no answer from {peer} to {o.Message.Id}, or anything else, in {AnswerTimeout.TotalSeconds:F0}s"
                    : $"the session with {peer} broke off before {o.Message.Id} was answered")
                : BackhaulSendResult.Defer($"session with {peer} ended before {o.Message.Id} was answered; it stays queued"));
            failOldest = false;
        }

        // Before the exchange nothing was taken from the batches: the
        // caller's first message is settled by whoever dialled.
        if (!Established) return;
        while (current is not null || work.Reader.TryRead(out current))
        {
            try
            {
                while (await current.NextAsync(CancellationToken.None) is { } message)
                {
                    await CompleteAsync(new Outgoing(message, current, Now),
                        BackhaulSendResult.Defer($"session with {peer} ended before {message.Id} went; it stays queued"));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't hand back the work left for {0}", peer);
            }
            current = null;
        }
    }

    private DateTimeOffset Now => TimeProvider.GetUtcNow();

    /// <summary>The agreed hold: the lower of the two ends' values.</summary>
    private TimeSpan Hold => TimeSpan.FromSeconds(Math.Min(OwnRules.HoldSeconds, PeerRules?.HoldSeconds ?? 0));

    private TimeSpan Quiet => Hold > MinQuiet ? Hold : MinQuiet;

    private TimeSpan Inactivity => Established && Hold + TimeSpan.FromSeconds(30) > InactivityTimeout
        ? Hold + TimeSpan.FromSeconds(30)
        : InactivityTimeout;

    /// <summary>Nothing unanswered either way and nothing more to send.</summary>
    private bool Idle => unanswered.Count == 0 && accepted.Count == 0 && current is null && !work.Reader.TryPeek(out _);

    private bool CanSend => Established && !quitting && unanswered.Count < Window;

    /// <summary>How long one of ours waits for its answer before we give
    /// up on it: a peer that answers other messages but never this one
    /// would otherwise keep the session, and the forwarder, waiting. The
    /// inactivity timeout, whatever the hold. It runs from when the message
    /// went or from the peer's last answer to anything of ours, whichever
    /// is later: on a slow link a full window can take longer than this to
    /// go on air, and while answers keep coming the link is working.</summary>
    private TimeSpan AnswerTimeout => InactivityTimeout;

    /// <summary>When <paramref name="o"/> gives up waiting for its answer.</summary>
    private DateTimeOffset AnswerDeadline(Outgoing o) => (o.SentAt > lastAnswered ? o.SentAt : lastAnswered) + AnswerTimeout;

    /// <summary>Acts on whichever timer has run out. True when it did something.</summary>
    private async Task<bool> CheckTimersAsync()
    {
        var now = Now;
        if (quitting)
        {
            if (now < quitDeadline) return false;
            End();
            return true;
        }
        if (now - lastHeard >= Inactivity)
        {
            logger.LogInformation("Nothing from {0} for {1:F0}s; closing the session", peer, Inactivity.TotalSeconds);
            Failure ??= Established ? null : $"no data from {peer} within {Inactivity.TotalSeconds:F0}s";
            End();
            return true;
        }
        if (unanswered.Any(o => now >= AnswerDeadline(o)))
        {
            // Nothing of ours answered for that long, though the link
            // isn't silent: the peer can't read us (the stream is out of
            // step), or the link can't carry our frames (at the edge of
            // range BPQ can resend the same frame for as long as the link
            // stays up). A new link starts both afresh. As for a break, the
            // oldest counts as failed, so the neighbour gets a cooldown,
            // and the rest go on the next session.
            logger.LogWarning("Nothing answered by {0} for {1:F0}s; ending the session", peer, AnswerTimeout.TotalSeconds);
            stuck = true;
            End();
            return true;
        }
        if (!dialled) return false;

        if (!ownSent && !awaitingRoutes && CrossedCall is { IsCompleted: true })
        {
            logger.LogInformation("Our call to {0} crossed its call to us, so no prompt is coming; sending our exchange now", peer);
            SendOwnExchange();
            return true;
        }

        if (ownSent && !Established && noiseSinceOwn > 0 && !heardDapps && now - ownSentAt >= PromptWait)
        {
            // Our rules went, and what came back wasn't DAPPS: a node's
            // command prompt, where BPQ leaves a link it has reset. (A
            // peer that sends us DAPPS traffic is a DAPPS node, whatever
            // else we heard: the tail of a message cut off when its node
            // moved the link, say. Its rules follow when it sees ours.)
            // Two known costs, both one cooldown at worst: a cut-off tail
            // with nothing DAPPS after it yet counts as other text, so a
            // peer whose rules take over 10 s to come back (a busy 1200
            // baud channel) is hung up on; and the 10 s run from when our
            // exchange was queued, not from when it went on air.
            logger.LogWarning("No exchange from {0} {1:F0}s after ours, only other text: whatever answered isn't a DAPPS session",
                peer, PromptWait.TotalSeconds);
            Failure = $"no exchange from {peer}, only other text";
            End();
            return true;
        }

        if (WaitingOnSilence && now - ownSentAt >= SilentPeerWait)
        {
            logger.LogWarning("Nothing at all from {0} {1:F0}s after our exchange: whatever answered the call isn't a DAPPS session",
                peer, SilentPeerWait.TotalSeconds);
            Failure = $"nothing from {peer} after our exchange";
            End();
            return true;
        }

        if (!ownSent && !awaitingRoutes && now - started >= PromptWait)
        {
            if (noiseBytes > 0)
            {
                logger.LogWarning("No DAPPSv1> prompt from {0}: whatever answered isn't a DAPPS node", peer);
                Failure = $"no DAPPSv1> prompt from {peer}";
                End();
                return true;
            }
            logger.LogInformation(
                "Nothing from {0} for {1:F0}s after connecting: assuming it dialled us at the same moment, sending our exchange",
                peer, PromptWait.TotalSeconds);
            SendOwnExchange();
            return true;
        }

        if (!Established) return false;
        if (now - started >= MaxLength)
        {
            BeginQuit($"after {MaxLength.TotalMinutes:F0} minutes; the next message dials afresh");
            return true;
        }
        if (Idle && now - lastTraffic >= Quiet)
        {
            BeginQuit($"after {Quiet.TotalSeconds:F0}s of quiet");
            return true;
        }
        return false;
    }

    private DateTimeOffset NextDeadline()
    {
        if (quitting) return quitDeadline;
        var next = lastHeard + Inactivity;
        foreach (var o in unanswered) next = Min(next, AnswerDeadline(o));
        if (!dialled) return next;
        if (!ownSent && !awaitingRoutes) next = Min(next, started + PromptWait);
        if (ownSent && !Established && noiseSinceOwn > 0 && !heardDapps) next = Min(next, ownSentAt + PromptWait);
        if (WaitingOnSilence) next = Min(next, ownSentAt + SilentPeerWait);
        if (Established)
        {
            next = Min(next, started + MaxLength);
            if (Idle) next = Min(next, lastTraffic + Quiet);
        }
        return next;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>Waits for the peer to say something, new work (when we
    /// can send it), or the next timer, and reads everything the peer
    /// has sent by then.</summary>
    private async Task WaitAsync(CancellationToken ct)
    {
        var delay = NextDeadline() - Now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        Task<bool> data;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            data = link.WaitForDataAsync(waiting.Token).AsTask();
            var waits = new List<Task> { data, Task.Delay(delay, TimeProvider, waiting.Token) };
            if (CanSend) waits.Add(work.Reader.WaitToReadAsync(waiting.Token).AsTask());
            if (dialled && !ownSent && !awaitingRoutes && CrossedCall is { IsCompleted: false } crossed) waits.Add(crossed);
            await Task.WhenAny(waits);
            await waiting.CancelAsync();
        }
        ct.ThrowIfCancellationRequested();

        if (data.IsFaulted) await data;     // the link failed under us
        if (!data.IsCompletedSuccessfully) return;
        if (!data.Result)
        {
            OnHungUp();
            return;
        }
        do
        {
            await ReadAndHandleLineAsync(ct);
            if (!done) await FillWindowAsync(ct);
        }
        while (!done && link.HasBufferedData);
    }

    private void OnHungUp()
    {
        if (!Established && dialled) Failure ??= promptSeen || ownSent
            ? $"{peer} hung up before the exchange"
            : $"no DAPPSv1> prompt from {peer}";
        logger.LogInformation("{0} hung up", peer);
        End();
    }

    private async Task ReadAndHandleLineAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct);
        if (line is null)
        {
            OnHungUp();
            return;
        }
        if (line.Length == 0) return;   // only line endings so far
        lastHeard = lastTraffic = Now;

        var verb = line.Split(' ', 2)[0];
        var id = line.Split(' ', 3) is { Length: >= 2 } parts ? parts[1] : null;

        if (quitting)
        {
            // Waiting for bye. Answers to what we sent still count, and a
            // payload the peer sent before it heard our quit still has to
            // be read past; nothing new is taken on.
            if (line == "bye") End();
            else if (IsQuit(line)) Say("bye\n", end: true);
            else if (id is not null && verb is "ack" or "no" or "bad" or "error") await OnAnswerAsync(verb, id, line, ct);
            else if (verb is "msg" or "data") await SkipPayloadAsync(verb, line, ct);
            return;
        }

        if (awaitingRoutes && TryTakeRouteLine(line))
        {
            if (!awaitingRoutes) await FinishRoutesAsync(ct);
            return;
        }

        switch (verb)
        {
            case ExchangeRules.Verb:
                await OnExchangeAsync(line);
                return;
            case "msg":
                if (await ReceiveMessageAsync(line, ct)) HeardExchangeTraffic();
                return;
            case "ihave":
                if (await ReceiveOfferAsync(line, ct)) HeardExchangeTraffic();
                return;
            case "data":
                if (await ReceiveDataAsync(line, ct)) HeardExchangeTraffic();
                return;
        }

        if (IsQuit(line))
        {
            logger.LogInformation("{0} has asked to quit", peer);
            peerQuit = true;
            Say("bye\n", end: true);
            return;
        }

        // Answers are taken before the exchange too: they arrive then when
        // BPQ has moved a link that was mid-exchange onto this session.
        // Only with a message id, though: before the exchange, a line such
        // as "no such command" is something else answering, not DAPPS.
        if (id is not null && (Established || IsMessageId(id)) && verb is "send" or "ack" or "no" or "bad" or "error")
        {
            await OnAnswerAsync(verb, id, line, ct);
            HeardExchangeTraffic();
            return;
        }

        if (line.EndsWith(Prompt, StringComparison.Ordinal))
        {
            if (dialled && !promptSeen && !ownSent) await OnPromptAsync(ct);
            return;
        }

        if (Established)
        {
            logger.LogInformation("Ignoring '{0}' from {1}", Printable(line), peer);
        }
        else if (!dialled)
        {
            // The answering node before the exchange: a probe or a person.
            var answer = Commands is null ? null : await Commands(line.Trim(), ct);
            if (answer is null)
            {
                logger.LogInformation("Unrecognised command {0}", Printable(line));
                Say("eh?\n", end: true);
                return;
            }
            Queue(answer);
        }
        else
        {
            // A caller hearing something other than DAPPS: a node banner.
            noiseBytes += line.Length;
            if (ownSent) noiseSinceOwn += line.Length;
        }
    }

    private static bool IsQuit(string line) => QuitCommands.Contains(line.Trim().ToLowerInvariant());

    /// <summary>A message id as DAPPS writes it: 7 hex digits.</summary>
    private static bool IsMessageId(string? id) => id is { Length: 7 } && id.All(char.IsAsciiHexDigit);

    /// <summary>
    /// A caller hearing exchange traffic before any prompt or rules: the
    /// peer is mid-exchange with the session this link had before (BPQ
    /// moved the link to ours). No prompt is coming, so our rules go now,
    /// and their new tag has the peer send its rules and anything
    /// unanswered again. Only for lines that are clearly DAPPS (a valid
    /// header, or an answer with a message id), so that whatever else
    /// answers a call still fails for want of a prompt.
    /// </summary>
    private void HeardExchangeTraffic()
    {
        heardDapps = true;
        if (!dialled || ownSent || awaitingRoutes || done) return;
        logger.LogInformation("{0} is mid-exchange with an earlier session on this link; sending our exchange now", peer);
        SendOwnExchange();
    }

    private void Say(string text, bool end)
    {
        Queue(text);
        if (end) End();
    }

    private async Task OnPromptAsync(CancellationToken ct)
    {
        promptSeen = true;
        if (ownSent) return;
        if (RouteGossip is not null)
        {
            try
            {
                if (await RouteGossip.ShouldPullAsync(peer, ct))
                {
                    // Command and response, before either side can send
                    // traffic that would interleave with the routes.
                    Queue("routes\n");
                    awaitingRoutes = true;
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Route gossip gate for {0} failed; not pulling routes", peer);
            }
        }
        SendOwnExchange();
    }

    /// <summary>A line of the <c>routes</c> answer: true when it was one.</summary>
    private bool TryTakeRouteLine(string line)
    {
        if (line == "end" || line == "eh?")
        {
            awaitingRoutes = false;
            return true;
        }
        if (DappsProtocolClient.ParseRouteLine(line) is { } route)
        {
            gossiped.Add(route);
            return true;
        }
        return false;
    }

    private async Task FinishRoutesAsync(CancellationToken ct)
    {
        try
        {
            if (gossiped.Count > 0) await RouteGossip!.ImportAsync(peer, gossiped, ct);
            await RouteGossip!.RecordPulledAsync(peer, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Importing routes from {0} failed", peer);
        }
        SendOwnExchange();
    }

    private void SendOwnExchange()
    {
        Queue(OwnRules.ToLine());
        ownSent = true;
        ownSentAt = Now;
        if (PeerRules is not null) Establish();
    }

    /// <summary>
    /// The peer's rules. The first time, and whenever it repeats a tag we
    /// know, we just take them. A new tag after we already had one means
    /// the peer's session object changed under the link (its node moved
    /// the link to a newer session): that one hasn't heard our rules or
    /// seen our messages, so both go again.
    /// </summary>
    private async Task OnExchangeAsync(string line)
    {
        var rules = ExchangeRules.Parse(line);
        var restarted = PeerRules is not null && !peerTags.Contains(rules.Tag);
        peerTags.Add(rules.Tag);
        PeerRules = rules;

        if (!ownSent)
        {
            if (!awaitingRoutes) SendOwnExchange();
            return;
        }
        if (restarted)
        {
            logger.LogInformation("{0} is on a new session (tag {1}); sending our rules and {2} unanswered message(s) again",
                peer, rules.Tag, unanswered.Count);
            Queue(OwnRules.ToLine());
            foreach (var o in unanswered.ToList()) await SendAsync(o);
            // Its offers we asked for belong to the old session object:
            // their payloads won't come, and it offers them again itself.
            accepted.Clear();
        }
        if (!Established) Establish();
    }

    private void Establish()
    {
        Established = true;
        logger.LogInformation(
            "Exchange with {0} under way: hold {1}s, it takes up to {2} bytes unasked{3}{4}",
            peer, Hold.TotalSeconds, PeerRules!.Inline,
            PeerRules.MaxBytes is { } max ? $", nothing over {max} bytes" : "",
            PeerRules.Dictionaries.Count > 0 ? $", compressed with z{string.Join(",z", PeerRules.Dictionaries)}" : ", plain only");
        Opened?.Invoke(this);
    }

    private void BeginQuit(string why)
    {
        logger.LogInformation("Ending the session with {0} {1}", peer, why);
        quitting = true;
        work.Writer.TryComplete();
        Queue("quit\n");
        quitDeadline = Now + TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// A <c>msg</c> or <c>data</c> that arrived after our <c>quit</c>: its
    /// payload is read and dropped, unanswered, so the lines after it are
    /// still read in step. The sender sends it again next time.
    /// </summary>
    private async Task SkipPayloadAsync(string verb, string line, CancellationToken ct)
    {
        int length;
        if (verb == "msg")
        {
            if (!IHaveValidator.TryGetWireLength(line, out _, out length) || length > MaxWireBytes)
            {
                End();
                return;
            }
        }
        else
        {
            var parts = line.Split(' ');
            if (parts.Length != 2 || !accepted.Remove(parts[1], out var offer))
            {
                End();
                return;
            }
            length = offer.Format == "p" ? offer.Length : offer.CompressedLength!.Value;
        }
        await ReadPayloadAsync(length, ct);
    }

    // ---- Sending ----

    private async Task FillWindowAsync(CancellationToken ct)
    {
        while (CanSend)
        {
            if (current is null && !work.Reader.TryRead(out current)) return;

            BackhaulMessage? message;
            try
            {
                message = await current.NextAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't take the next message for {0}", peer);
                message = null;
            }
            if (message is null)
            {
                current = null;
                continue;
            }

            var o = new Outgoing(message, current, Now);
            unanswered.Add(o);
            if (unanswered.Count(u => u.Message.Id == message.Id) > 1)
            {
                await CompleteAsync(o, BackhaulSendResult.Defer($"{message.Id} is already on its way to {peer}"));
                continue;
            }
            await SendAsync(o);
        }
    }

    /// <summary>
    /// Put one of ours on the wire, as the peer's rules say: not at all if
    /// it's bigger than its <c>max</c>, compressed when we may and it
    /// holds our dictionary, and as <c>msg</c> with its payload when that
    /// fits its <c>inline</c>, else as an offer.
    /// </summary>
    private async Task SendAsync(Outgoing o)
    {
        var rules = PeerRules!;
        var message = o.Message;
        if (rules.MaxBytes is { } max && message.Payload.Length > max)
        {
            var refusal = $"{peer} takes nothing over {max} bytes and {message.Id} is {message.Payload.Length}";
            logger.LogInformation("Not sending {0}: {1}", message.Id, refusal);
            await CompleteAsync(o, BackhaulSendResult.Refuse(refusal));
            return;
        }

        var compressed = settings.Compress && !plainOnly && !o.Retried
            && rules.Dictionaries.Contains(PayloadCompression.CurrentDictionaryVersion)
            ? PayloadCompression.TryCompress(message.Payload)
            : null;
        o.Format = compressed?.Format ?? "p";
        o.Wire = compressed?.Bytes ?? message.Payload;

        var inline = o.Wire.Length <= rules.Inline;
        string header;
        try
        {
            header = OfferLine.Build(inline ? "msg" : "ihave", message, o.Format, compressed?.Bytes.Length);
        }
        catch (ArgumentException ex)
        {
            await CompleteAsync(o, BackhaulSendResult.Fail(ex.Message));
            return;
        }
        if (compressed is not null)
        {
            logger.LogInformation("Sending {0} as fmt={1}: {2} bytes compressed to {3}",
                message.Id, o.Format, message.Payload.Length, o.Wire.Length);
        }
        if (inline)
        {
            Queue(header, o.Wire);
            o.State = OutgoingState.Sent;
        }
        else
        {
            Queue(header);
            o.State = OutgoingState.Offered;
        }
        o.SentAt = Now;
    }

    private async Task OnAnswerAsync(string verb, string id, string line, CancellationToken ct)
    {
        var o = unanswered.FirstOrDefault(u => u.Message.Id == id);
        if (o is null)
        {
            logger.LogInformation("'{0}' from {1} for nothing of ours in flight; ignored", Printable(line), peer);
            return;
        }
        lastAnswered = Now;

        switch (verb)
        {
            case "send" when o.State == OutgoingState.Offered && !quitting:
                Queue($"data {id}\n", o.Wire);
                o.State = OutgoingState.Sent;
                o.SentAt = Now;
                return;
            case "send":
                logger.LogInformation("{0} asked for {1}, which isn't waiting on an offer; ignored", peer, id);
                return;
            case "ack":
                if (o.State == OutgoingState.Offered)
                {
                    logger.LogInformation("{0} already has {1}; counting it delivered", peer, id);
                }
                await CompleteAsync(o, BackhaulSendResult.Ok());
                return;
            case "no":
                var reason = line.Split(' ', 3) is { Length: 3 } parts ? parts[2] : "no reason given";
                logger.LogInformation("{0} won't take {1}: {2}", peer, id, reason);
                await CompleteAsync(o, BackhaulSendResult.Refuse($"{peer} refused {id}: {reason}"));
                return;
            default:
                // bad or error: one more go, plain; or, when the session
                // is ending, next time.
                if (!o.Retried && quitting)
                {
                    await CompleteAsync(o, BackhaulSendResult.Defer($"{peer} answered {verb} to {id} as the session ended; it goes again next time"));
                    return;
                }
                if (!o.Retried)
                {
                    o.Retried = true;
                    if (o.Format != "p" && !plainOnly)
                    {
                        plainOnly = true;
                        logger.LogWarning(
                            "{0} answered {1} to {2} as fmt={3}; sending it plain, and the rest of this session too. If this "
                            + "keeps happening, something on the path isn't passing binary data through, and compression "
                            + "should be turned off for this neighbour.",
                            peer, verb, id, o.Format);
                    }
                    else
                    {
                        logger.LogInformation("{0} answered {1} to {2}; sending it once more", peer, verb, id);
                    }
                    await SendAsync(o);
                    return;
                }
                await CompleteAsync(o, BackhaulSendResult.Fail(
                    verb == "error" ? $"offer rejected for {id}" : $"payload rejected for {id}"));
                return;
        }
    }

    private async Task CompleteAsync(Outgoing o, BackhaulSendResult result)
    {
        unanswered.Remove(o);
        try
        {
            await o.Batch.CompleteAsync(o.Message, result, Now - o.Started, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording what happened to {0} failed", o.Message.Id);
        }
    }

    // ---- Receiving ----

    /// <summary>Answers an <c>ihave</c>. True if it was a valid one.</summary>
    private async Task<bool> ReceiveOfferAsync(string line, CancellationToken ct)
    {
        var result = IHaveValidator.Validate(line);
        if (!result.IsValid)
        {
            logger.LogWarning("Refusing an offer from {0}: {1}", peer, result.Error);
            Queue($"error {result.Id ?? "??"}\n");
            return false;
        }
        var offer = result.Offer!;
        if (WontTake(offer) is { } why)
        {
            Queue($"no {offer.Id} {why}\n");
            return true;
        }
        if (await inbox!.HasAsync(offer.Id, offer.Salt, offer.Length, ct))
        {
            // We have it already (the sender restarted, or lost our ack):
            // say so now, and the payload doesn't go over the air again.
            logger.LogInformation("Offered {0}, which we already have; answering ack", offer.Id);
            Queue($"ack {offer.Id}\n");
            return true;
        }
        logger.LogInformation("Accepting offer {0} (len={1}, fmt={2}, dst={3})", offer.Id, offer.Length, offer.Format, offer.Destination);
        accepted[offer.Id] = offer;
        Queue($"send {offer.Id}\n");
        return true;
    }

    /// <summary>Reads and answers a <c>msg</c>. True if its header was valid.</summary>
    private async Task<bool> ReceiveMessageAsync(string line, CancellationToken ct)
    {
        if (!IHaveValidator.TryGetWireLength(line, out var id, out var wireLength) || wireLength > MaxWireBytes)
        {
            // We can't tell where its payload ends, so nothing after it
            // can be read either.
            logger.LogWarning("Can't tell how long the payload after '{0}' is; ending the session with {1}", Printable(line), peer);
            End();
            return false;
        }
        var wire = await ReadPayloadAsync(wireLength, ct);
        var result = IHaveValidator.Validate(line);
        if (!result.IsValid)
        {
            logger.LogWarning("Refusing a message from {0}: {1}", peer, result.Error);
            Queue($"error {id}\n");
            return false;
        }
        var offer = result.Offer!;
        if (WontTake(offer) is { } why)
        {
            Queue($"no {offer.Id} {why}\n");
            return true;
        }
        await AcceptPayloadAsync(offer, wire, ct);
        return true;
    }

    /// <summary>Reads and answers a <c>data</c>. True if it was for an offer we accepted.</summary>
    private async Task<bool> ReceiveDataAsync(string line, CancellationToken ct)
    {
        var parts = line.Split(' ');
        if (parts.Length != 2 || !accepted.Remove(parts[1], out var offer))
        {
            logger.LogWarning("'{0}' from {1} isn't for an offer we accepted, so its length is unknown; ending the session",
                Printable(line), peer);
            End();
            return false;
        }
        var wire = await ReadPayloadAsync(offer.Format == "p" ? offer.Length : offer.CompressedLength!.Value, ct);
        await AcceptPayloadAsync(offer, wire, ct);
        return true;
    }

    /// <summary>Why we won't take a message, or null when we will.</summary>
    private string? WontTake(IHaveOffer offer)
    {
        if (settings.MaxBytes is { } max && offer.Length > max) return $"larger than {max} bytes";
        if (inbox is null) return "not taking messages";
        return null;
    }

    private async Task AcceptPayloadAsync(IHaveOffer offer, byte[] wire, CancellationToken ct)
    {
        byte[] payload;
        try
        {
            payload = PayloadCompression.Decode(offer.Format, wire, offer.Length);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("Payload for {0} from {1} does not decode: {2}", offer.Id, peer, ex.Message);
            Queue($"bad {offer.Id}\n");
            return;
        }

        if (DappsMessage.ComputeHash(payload, offer.Salt)[..7] != offer.Id)
        {
            logger.LogWarning("Payload for {0} from {1} doesn't hash to its id", offer.Id, peer);
            HashMismatch?.Invoke(offer.Id);
            Queue($"bad {offer.Id}\n");
            return;
        }

        await inbox!.DeliverAsync(new BackhaulMessage(
            Id: offer.Id,
            Destination: offer.Destination,
            Salt: offer.Salt,
            Ttl: offer.Ttl,
            Payload: payload,
            Headers: offer.AdditionalHeaders.Count > 0 ? offer.AdditionalHeaders : null,
            Originator: offer.Originator,
            MasterId: offer.MasterId,
            FragmentIndex: offer.Fragment?.Index,
            FragmentTotal: offer.Fragment?.Total,
            StreamId: offer.StreamId,
            StreamSeq: offer.StreamSeq,
            StreamGapTimeoutSeconds: offer.StreamGapTimeoutSeconds), peer, ct);
        Delivered++;
        logger.LogInformation("Got {0} from {1} ({2} bytes, fmt={3})", offer.Id, peer, payload.Length, offer.Format);
        Queue($"ack {offer.Id}\n");
    }

    // ---- The link ----

    private void Queue(string text) => outgoing.Write(Encoding.UTF8.GetBytes(text));

    private void Queue(string text, byte[] payload)
    {
        Queue(text);
        outgoing.Write(payload);
    }

    /// <summary>Everything queued since the last write, in one write.</summary>
    private async Task FlushAsync(CancellationToken ct)
    {
        if (outgoing.Length == 0) return;
        var bytes = outgoing.ToArray();
        outgoing.SetLength(0);
        await link.WriteAsync(bytes, ct);
        await link.FlushAsync(ct);
        lastTraffic = Now;
    }

    /// <summary>
    /// The next line, without its ending (<c>\n</c>, <c>\r</c> or both).
    /// Empty when only line endings had arrived, so the loop can go back
    /// to waiting rather than block here; null at the end of the stream.
    /// </summary>
    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (buffer.Count == 0 && !link.HasBufferedData)
            {
                // Only reached after skipping line endings: don't wait
                // here for a line that may be a long time coming.
                if (!await link.WaitForDataAsync(ct)) return null;
                if (!link.HasBufferedData) return "";
            }
            var n = await ReadWithTimeoutAsync(one, ct);
            if (n == 0) return buffer.Count > 0 ? Encoding.UTF8.GetString(buffer.ToArray()) : null;
            if (one[0] is (byte)'\n' or (byte)'\r')
            {
                if (buffer.Count == 0)
                {
                    if (!link.HasBufferedData) return "";
                    continue;
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            buffer.Add(one[0]);
            if (buffer.Count >= MaxLineBytes) return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private async Task<byte[]> ReadPayloadAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await ReadWithTimeoutAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new IOException($"the link ended after {read} of {count} payload bytes");
            read += n;
        }
        lastHeard = lastTraffic = Now;
        return buffer;
    }

    private async ValueTask<int> ReadWithTimeoutAsync(Memory<byte> buffer, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Inactivity);
        try
        {
            return await link.ReadAsync(buffer, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no data from {peer} within {Inactivity.TotalSeconds:F0}s");
        }
    }

    /// <summary>A line as it can go in a log: printable ASCII only, and not too long.</summary>
    private static string Printable(string line)
    {
        var chars = line.Take(80).Select(c => c is >= ' ' and <= '~' ? c : '?');
        return new string([.. chars]) + (line.Length > 80 ? "..." : "");
    }

    private enum OutgoingState { Offered, Sent }

    private sealed class Outgoing(BackhaulMessage message, IBackhaulBatch batch, DateTimeOffset started)
    {
        public BackhaulMessage Message { get; } = message;
        public IBackhaulBatch Batch { get; } = batch;
        public DateTimeOffset Started { get; } = started;

        /// <summary>When it last went on the wire: the answer timeout runs from here.</summary>
        public DateTimeOffset SentAt { get; set; } = started;
        public OutgoingState State { get; set; }
        public string Format { get; set; } = "p";
        public byte[] Wire { get; set; } = [];
        public bool Retried { get; set; }
    }
}
