using dapps.client;
using dapps.client.Transport.Agw;
using Microsoft.Extensions.Logging;
using System.Text;

try
{
    var loggerFactory = LoggerFactory.Create(builder =>
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss.fff ";
        }));

    var logger = loggerFactory.CreateLogger<Program>();

    // Adjust these for your local node:
    const string nodeHost = "debian1";
    const int agwPort = 8000;
    const string localCallsign = "M0LTE-7";
    const string remoteCallsign = "Q0BBB";
    const int bearerPort = 0;          // 0-indexed AGW port byte (BPQ user-facing port 1)
    const string destination = "testqueue@gb7rdg";

    await using var links = new StandaloneAgwLinkSource(nodeHost, agwPort, localCallsign, loggerFactory);
    var transport = new AgwOutboundTransport(links, loggerFactory);

    logger.LogInformation("Connecting {local}->{remote} on AGW port {p}", localCallsign, remoteCallsign, bearerPort);
    await using var connection = await transport.ConnectAsync(localCallsign, remoteCallsign, bearerPort, CancellationToken.None);

    var protocol = new DappsProtocolClient(connection.Stream, loggerFactory);

    if (!await protocol.ReadInitialPromptAsync(CancellationToken.None))
    {
        logger.LogError("Did not see DAPPSv1> prompt from remote");
        return;
    }
    logger.LogInformation("Got remote DAPPS prompt");

    var payload = "hello world";
    var payloadBytes = Encoding.UTF8.GetBytes(payload);
    var salt = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
    var id = DappsMessage.ComputeHash(payloadBytes, salt)[..7];

    if (!await protocol.OfferMessageAsync(id, salt, DappsMessage.MessageFormat.Plain, destination, payloadBytes.Length, CancellationToken.None))
    {
        logger.LogError("Failed to offer message {id}", id);
        return;
    }
    logger.LogInformation("Offered message {id}", id);

    if (!await protocol.SendMessageAsync(id, payloadBytes, CancellationToken.None))
    {
        logger.LogError("Failed to send message {id}", id);
        return;
    }
    logger.LogInformation("Sent message {id}", id);
}
finally
{
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey(true);
}
