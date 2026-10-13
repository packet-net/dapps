using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace dapps.core.Services;

/// <summary>
/// RHPv2 <c>bind</c>, coping with XRouter 505o's reply to it, which isn't
/// JSON: the error text has lost its label and quotes, and the type its
/// "Reply".
///
///     505c: {"type": "bindReply", "id": 3, "handle": 1, "errCode": 0, "errText": "Ok"}
///     505o: {"type": "bind", "id": 3, "handle": 1, "errCode": 0, Ok}
///
/// (packethacking/xrouter-container#2; the author has fixed it for the next
/// build.) RhpClient can't read that reply, so it never completes the
/// bind's request; it passes the text on as an unknown message instead.
/// Left alone, the inbound listener would wait for its bind forever and
/// never listen. This watches for that text about the socket being bound
/// and takes its errCode as the answer, so the bind succeeds or fails as
/// the node said.
/// </summary>
internal static partial class Rhpv2Bind
{
    /// <summary>Bind <paramref name="local"/> to <paramref name="handle"/> on
    /// all of the node's ports. Throws <see cref="RhpServerException"/> when
    /// the node refuses, as <see cref="RhpClient.BindAsync"/> does.</summary>
    /// <returns>True when the node's reply wasn't JSON and was read here.</returns>
    public static async Task<bool> BindAsync(RhpClient rhp, int handle, string local, CancellationToken ct)
    {
        var unreadable = new TaskCompletionSource<(int Code, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUnknown(object? sender, RhpUnknownEventArgs e)
        {
            if (TryReadUnreadableReply(e.Message, handle, out var code, out var text)) unreadable.TrySetResult((code, text));
        }

        using var bindCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        rhp.UnknownReceived += OnUnknown;
        try
        {
            var bind = rhp.BindAsync(handle, local, port: null, bindCt.Token);
            if (await Task.WhenAny(bind, unreadable.Task) == bind)
            {
                await bind;
                return false;
            }

            // The node has answered, in a form RhpClient can't match to the
            // request: stop it waiting for one it can.
            await bindCt.CancelAsync();
            try { await bind; }
            catch (OperationCanceledException) { /* as asked */ }

            var (code, text) = await unreadable.Task;
            if (code != 0) throw new RhpServerException(code, text);
            return true;
        }
        finally
        {
            rhp.UnknownReceived -= OnUnknown;
        }
    }

    /// <summary>
    /// True when <paramref name="message"/> is a frame RhpClient couldn't
    /// parse that reads as 505o's reply to a bind on <paramref name="handle"/>.
    /// </summary>
    internal static bool TryReadUnreadableReply(RhpMessage message, int handle, out int code, out string text)
    {
        code = 0;
        text = "";
        if (message is not UnknownMessage { Raw: var raw }
            || raw["raw"] is not JsonValue value
            || !value.TryGetValue(out string? frame))
        {
            return false;
        }

        var match = UnreadableBindReply().Match(frame);
        if (!match.Success || !int.TryParse(match.Groups["handle"].ValueSpan, out var h) || h != handle)
        {
            return false;
        }

        code = int.Parse(match.Groups["code"].ValueSpan);
        text = match.Groups["text"].Value.Trim().Trim('"');
        return true;
    }

    // The bind's type, then "handle" and "errCode" as numbers, then the
    // error text with or without its label and quotes, then the end.
    [GeneratedRegex("""^\s*\{\s*"type"\s*:\s*"bind(?:Reply)?"\s*,.*?"handle"\s*:\s*(?<handle>\d+)\s*,\s*"errCode"\s*:\s*(?<code>\d+)\s*,\s*(?:"errText"\s*:\s*)?(?<text>[^}]*?)\s*\}\s*$""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UnreadableBindReply();
}
