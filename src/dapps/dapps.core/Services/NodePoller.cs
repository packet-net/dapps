using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using Microsoft.Extensions.Logging;

namespace dapps.core.Services;

/// <summary>
/// Plan F3b - one poll of a neighbour for mail it holds for us. Dials,
/// runs an <see cref="ExchangeSession"/> with nothing of our own to send,
/// takes whatever the neighbour sends through
/// <see cref="IBackhaulInbox.DeliverAsync"/>, and hangs up once the link
/// has been quiet for the agreed hold. Stateless - the same instance can
/// serve many concurrent polls.
///
/// Mirror of <see cref="NodeProber"/> for the C5.1-style reachability
/// case; the difference is that this one actually collects the
/// remote's queued mail, where the prober just confirms the session
/// reaches the prompt.
/// </summary>
public sealed class NodePoller(
    IDappsOutboundTransport transport,
    IBackhaulInbox inbox,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ILogger<NodePoller> logger,
    IRouteGossipPort? routeGossip = null,
    ExchangePolicy? exchangePolicy = null)
{
    /// <summary>Outcome of a single poll. Failure is captured rather
    /// than thrown - the scheduler catches per-callsign failures so
    /// one unreachable neighbour doesn't tank the whole sweep.</summary>
    public sealed record PollResult(
        string Callsign,
        bool Success,
        int MessagesDrained,
        string Error,
        DateTime At);

    /// <summary>The shortest quiet spell before hanging up
    /// (<see cref="ExchangeSession.MinQuiet"/>).</summary>
    public TimeSpan MinQuiet { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<PollResult> PollAsync(
        string localCallsign,
        string remoteCallsign,
        int bearerPort,
        CancellationToken ct,
        ConnectScript? connectScript = null)
    {
        var at = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            await using var connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: remoteCallsign,
                bearerPort: bearerPort,
                stoppingToken: ct);
            using var stream = new PumpedReadStream(connection.Stream);

            if (connectScript is not null)
            {
                try
                {
                    await ConnectScriptRunner.RunAsync(stream, connectScript, logger, ct);
                }
                catch (Exception ex) when (ex is ConnectScriptException or EndOfStreamException)
                {
                    return new PollResult(remoteCallsign, false, 0, $"connect-script: {ex.Message}", at);
                }
            }

            var settings = exchangePolicy is null ? new ExchangeSettings() : await exchangePolicy.ForPeerAsync(remoteCallsign, ct);
            var session = new ExchangeSession(stream, remoteCallsign, dialled: true, settings, inbox, loggerFactory)
            {
                TimeProvider = timeProvider,
                MinQuiet = MinQuiet,
                PromptConsumed = connectScript is not null,
                RouteGossip = routeGossip,
            };
            await session.RunAsync(ct);
            if (!session.Established)
            {
                var error = session.Failure ?? "no session";
                logger.LogInformation("Poll failed: {0} ({1})", remoteCallsign, error);
                return new PollResult(remoteCallsign, false, 0, error, at);
            }

            logger.LogInformation("Poll ok: {0} sent us {1} message(s)", remoteCallsign, session.Delivered);
            return new PollResult(remoteCallsign, true, session.Delivered, "", at);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation - re-throw so the scheduler
            // can exit cleanly on shutdown.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogInformation("Poll failed: {0} ({1})", remoteCallsign, ex.Message);
            return new PollResult(remoteCallsign, false, 0, ex.Message, at);
        }
    }
}
