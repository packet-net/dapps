using dapps.client;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace dapps.core.Controllers;

[ApiController]
[Route("[controller]")]
public class MessageController(Database database, ILogger<MessageController> logger, OutboundMessageManager outboundMessageManager) : ControllerBase
{
    [HttpPost("message")]
    public async Task<IActionResult> Post(DappsMessageModel dappsMessageModel)
    {
        // The destination goes on the air as it is (#313).
        if (MessageFields.DestinationError(dappsMessageModel.Destination) is { } error)
        {
            return BadRequest($"Destination: {error}");
        }
        var dappsMessage = new DappsMessage
        {
            Payload = Encoding.UTF8.GetBytes(dappsMessageModel.TextPayload),
            Destination = dappsMessageModel.Destination,
            Salt = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds
        };

        await database.SaveMessage(
            dappsMessage.Id,
            dappsMessage.Payload,
            dappsMessage.Salt,
            dappsMessage.Destination,
            sourceCallsign: "",
            "{}",
            ttl: null);
        return Ok();
    }

    [HttpPost("dorun")]
    public async Task<IActionResult> DoRun()
    {
        logger.LogInformation("Starting a run");
        await outboundMessageManager.DoRun();
        logger.LogInformation("Run completed");
        return Ok();
    }
}

public class DappsMessageModel
{
    public string Destination { get; set; } = "";
    public string TextPayload { get; set; } = "";
}