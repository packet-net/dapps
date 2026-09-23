using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using dapps.client.Backhaul;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// Test doubles for the AGW seam. A <see cref="FakeAgwHost"/> is a TCP
/// listener that plays BPQ's AGW socket; each accepted connection is a
/// <see cref="FakeAgwSocket"/> that reads and writes raw
/// <see cref="AgwFrame"/>s, so a test can script exactly the frame
/// sequence BPQ would emit (including the ones it gets subtly wrong,
/// like a 'd' carrying the wrong port) and observe every frame dapps
/// sends back.
/// </summary>
internal sealed class FakeAgwHost : IDisposable
{
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);

    public FakeAgwHost() => listener.Start();

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>True when a client has dialled and is waiting to be accepted.</summary>
    public bool Pending() => listener.Pending();

    public async Task<FakeAgwSocket> AcceptAsync(CancellationToken ct)
    {
        var tcp = await listener.AcceptTcpClientAsync(ct).AsTask().WaitAsync(FrameTimeout, ct);
        return new FakeAgwSocket(tcp);
    }

    public void Dispose() => listener.Stop();
}

internal sealed class FakeAgwSocket : IDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream wire;
    private readonly AgwFrameTransport framing;

    public FakeAgwSocket(TcpClient tcp)
    {
        this.tcp = tcp;
        wire = tcp.GetStream();
        framing = new AgwFrameTransport(wire);
    }

    /// <summary>Every frame read from dapps so far, in order.</summary>
    public List<AgwFrame> Received { get; } = [];

    public async Task<AgwFrame> ReadFrameAsync(CancellationToken ct)
    {
        var frame = await framing.ReadFrameAsync(ct).WaitAsync(FakeAgwHost.FrameTimeout, ct);
        Received.Add(frame);
        return frame;
    }

    public async Task<AgwFrame> ReadUntilAsync(char kind, CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReadFrameAsync(ct);
            if (frame.Kind == kind) return frame;
        }
    }

    /// <summary>Reads until the next 'D' frame and returns its payload as text.</summary>
    public async Task<string> ReadTextAsync(CancellationToken ct)
        => Encoding.UTF8.GetString((await ReadUntilAsync('D', ct)).Payload);

    /// <summary>Reads the 'X' registration dapps sends first on every connection.</summary>
    public async Task<AgwFrame> ExpectRegisterAsync(CancellationToken ct)
    {
        var frame = await ReadFrameAsync(ct);
        if (frame.Kind != 'X') throw new InvalidOperationException($"expected 'X' registration, got '{frame.Kind}'");
        return frame;
    }

    /// <summary>Writes the frames in ONE TCP write so they land
    /// back-to-back, the way BPQ's socket flush batches them.</summary>
    public async Task WriteAsync(CancellationToken ct, params AgwFrame[] frames)
    {
        var bytes = frames.SelectMany(f => f.ToBytes()).ToArray();
        await wire.WriteAsync(bytes, ct);
        await wire.FlushAsync(ct);
    }

    /// <summary>Whatever dapps has sent that is still unread, after a
    /// short real-time settle for stragglers.</summary>
    public async Task<List<AgwFrame>> DrainAsync(CancellationToken ct, TimeSpan? settle = null)
    {
        await Task.Delay(settle ?? TimeSpan.FromMilliseconds(300), ct);
        var got = new List<AgwFrame>();
        while (wire.DataAvailable) got.Add(await ReadFrameAsync(ct));
        return got;
    }

    /// <summary>Hard-drops the socket, the way BPQ going away looks to dapps.</summary>
    public void Close() => tcp.Close();

    public void Dispose() => tcp.Dispose();

    // Frames as BPQ's AGWAPI.c builds them for an application-side
    // session: CallFrom is the peer, CallTo is our APPL callsign, on
    // both the inbound 'C' and every 'D' / 'd' that follows.

    public static AgwFrame Connect(string remote, string local, byte port) =>
        new(port, 'C', 0, remote, local, Encoding.ASCII.GetBytes($"*** CONNECTED To Station {remote}\r\0"));

    public static AgwFrame Disconnect(string remote, string local, byte port) =>
        new(port, 'd', 0, remote, local, Encoding.ASCII.GetBytes($"*** DISCONNECTED From Station {remote}\r\0"));

    public static AgwFrame Data(string remote, string local, byte port, string text) =>
        new(port, 'D', 0xF0, remote, local, Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// Hosts an <see cref="AgwInboundService"/> against a <see cref="FakeAgwHost"/>
/// with a capturing logger, so tests can assert on what the service
/// logged as well as what it put on the wire. The caller owns the
/// SQLite override (see <see cref="SqliteOverridePathCollection"/>).
/// </summary>
internal sealed class AgwInboundServiceHarness : IAsyncDisposable
{
    public AgwInboundServiceHarness(
        string localCallsign, TimeProvider? clock = null, IBackhaulInbox? inbox = null, PeerSessionRegistry? peerSessions = null)
    {
        Host = new FakeAgwHost();
        Options = new MutableOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = localCallsign,
            NodeHost = "127.0.0.1",
            AgwPort = Host.Port,
        });
        Logs = new CapturingLoggerFactory();
        Metrics = new OperationalMetrics(clock ?? TimeProvider.System);
        Service = new AgwInboundService(
            Options,
            new Database(NullLogger<Database>.Instance, Options),
            inbox ?? new NullBackhaulInbox(),
            Logs,
            new Logger<AgwInboundService>(Logs),
            metrics: Metrics,
            timeProvider: clock,
            peerSessions: peerSessions);
    }

    public FakeAgwHost Host { get; }
    public AgwInboundService Service { get; }
    public CapturingLoggerFactory Logs { get; }
    public OperationalMetrics Metrics { get; }

    /// <summary>The options the service watches; <see cref="MutableOptionsMonitor{T}.Set"/>
    /// plays a /Config save.</summary>
    public MutableOptionsMonitor<SystemOptions> Options { get; }

    /// <summary>A fresh copy of the current options, the way a /Config save
    /// that changed nothing relevant still republishes them.</summary>
    public SystemOptions SameOptions() => new()
    {
        Callsign = Options.CurrentValue.Callsign,
        NodeHost = Options.CurrentValue.NodeHost,
        AgwPort = Options.CurrentValue.AgwPort,
    };

    /// <summary>Starts the service, accepts its connection and consumes the 'X' registration.</summary>
    public async Task<FakeAgwSocket> StartAsync(CancellationToken ct)
    {
        await Service.StartAsync(ct);
        var socket = await Host.AcceptAsync(ct);
        await socket.ExpectRegisterAsync(ct);
        return socket;
    }

    public async ValueTask DisposeAsync()
    {
        await Service.StopAsync(CancellationToken.None);
        Host.Dispose();
    }
}

internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// An <see cref="ILoggerFactory"/> that records every entry and lets a
/// test await one, which turns "the service noticed X" into a
/// deterministic wait instead of a sleep.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly Lock gate = new();
    private readonly List<LogEntry> entries = [];
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (gate) return entries.ToList(); }
    }

    public IEnumerable<LogEntry> Warnings => Entries.Where(e => e.Level >= LogLevel.Warning);

    public bool Any(string fragment) => Entries.Any(e => e.Message.Contains(fragment, StringComparison.Ordinal));

    /// <summary>Returns the first entry matching <paramref name="predicate"/>,
    /// waiting for it to be logged if it has not been yet.</summary>
    public async Task<LogEntry> WaitForAsync(Func<LogEntry, bool> predicate, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(FakeAgwHost.FrameTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        while (true)
        {
            Task signal;
            lock (gate)
            {
                var hit = entries.FirstOrDefault(predicate);
                if (hit is not null) return hit;
                signal = changed.Task;
            }
            await signal.WaitAsync(linked.Token);
        }
    }

    public Task<LogEntry> WaitForAsync(string fragment, CancellationToken ct)
        => WaitForAsync(e => e.Message.Contains(fragment, StringComparison.Ordinal), ct);

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private void Record(LogEntry entry)
    {
        lock (gate)
        {
            entries.Add(entry);
            var previous = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    private sealed class Sink(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Record(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}

/// <summary>
/// A <see cref="FakeTimeProvider"/> that also reports every timer the
/// code under test creates. That turns "the service is now waiting for
/// its backoff" into something a test can await, so the fake clock is
/// only ever advanced once the wait it is meant to satisfy exists -
/// no sleeping, no guessing.
/// </summary>
internal sealed class ObservableTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider inner;
    private readonly Channel<TimeSpan> created = Channel.CreateUnbounded<TimeSpan>();

    public ObservableTimeProvider(DateTimeOffset? start = null)
    {
        inner = start is { } s ? new FakeTimeProvider(s) : new FakeTimeProvider();
    }

    public void Advance(TimeSpan by) => inner.Advance(by);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
    public override long GetTimestamp() => inner.GetTimestamp();
    public override long TimestampFrequency => inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        created.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>Completes once a timer with exactly this due time has been
    /// created since the last call, i.e. the code under test has started
    /// the wait the caller is about to satisfy with <see cref="Advance"/>.</summary>
    public async Task WaitForTimerAsync(TimeSpan dueTime, CancellationToken ct)
    {
        while (true)
        {
            var seen = await created.Reader.ReadAsync(ct).AsTask().WaitAsync(FakeAgwHost.FrameTimeout, ct);
            if (seen == dueTime) return;
        }
    }
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>An options monitor whose value can be replaced, firing
/// OnChange the way SystemOptionsStore does after a /Config save.</summary>
internal sealed class MutableOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
{
    private readonly Lock gate = new();
    private readonly List<Action<T, string?>> listeners = [];

    public T CurrentValue { get; private set; } = initial;
    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        lock (gate) listeners.Add(listener);
        return new Subscription(this, listener);
    }

    public void Set(T value)
    {
        Action<T, string?>[] snapshot;
        lock (gate)
        {
            CurrentValue = value;
            snapshot = listeners.ToArray();
        }
        foreach (var listener in snapshot) listener(value, null);
    }

    private sealed class Subscription(MutableOptionsMonitor<T> owner, Action<T, string?> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate) owner.listeners.Remove(listener);
        }
    }
}

internal sealed class NullBackhaulInbox : IBackhaulInbox
{
    public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
        => Task.CompletedTask;
}
