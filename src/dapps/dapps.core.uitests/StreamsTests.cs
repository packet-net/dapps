using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Streams page: per-(remote, stream-id) send/receive cursors, and the
/// operator's skip for a stalled stream (#290).
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class StreamsTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task Streams_Empty_State_Renders_Both_Halves()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Streams");

        var headings = await page.Locator(".panel h3").AllInnerTextsAsync();
        headings.Should().Contain("Send state");
        headings.Should().Contain("Receive state");

        // On a fresh node neither half has any rows; the model emits
        // the .empty placeholder.
        (await page.Locator(".panel .empty").CountAsync())
            .Should().Be(2, "Send and Receive panels each emit an empty placeholder when no streams exist");
    }

    /// <summary>
    /// #290: a strict stream stalled on a message that never comes can be
    /// skipped on to the next waiting one. The stream id comes from a
    /// remote node, so one shaped to break out of a quoted handler must
    /// not run script. The skip is listed under Dropped.
    /// </summary>
    [Fact]
    public async Task Streams_SkipToNext_DeliversTheWaitingMessage_AndIsListedUnderDropped()
    {
        const string sender = "G0SKP";
        const string streamId = "s1');window.__pwned=1;('";
        const string id = "5ee0003";
        var key = dapps.core.Models.DbStreamRecvState.MakeKey(LoggedInWebAppFixture.Callsign, sender, streamId);
        using (var db = new SQLite.SQLiteConnection(app.DatabasePath))
        {
            db.Insert(new dapps.core.Models.DbStreamRecvState
            {
                Key = key, LocalCallsign = LoggedInWebAppFixture.Callsign, SenderCallsign = sender,
                StreamId = streamId, NextExpectedSeq = 1, LastReceivedAt = DateTime.UtcNow,
            });
            db.Insert(new dapps.core.Models.DbMessage
            {
                Id = id, Payload = "three"u8.ToArray(), Salt = 1,
                Destination = "uitestskip@" + LoggedInWebAppFixture.Callsign,
                SourceCallsign = sender, OriginatorCallsign = sender, AdditionalProperties = "{}",
                CreatedAt = DateTime.UtcNow, StreamId = streamId, StreamSeq = 3,
                StreamGapTimeoutSeconds = 0, PendingInOrder = true,
            });
        }
        try
        {
            await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
            var page = await ctx.NewPageAsync();
            string? prompt = null;
            page.Dialog += async (_, dialog) => { prompt = dialog.Message; await dialog.AcceptAsync(); };
            await page.GotoAsync($"{app.BaseUrl}/Streams");

            var row = page.Locator("#recv-body tr", new PageLocatorOptions { HasText = sender });
            await row.Locator("button.skip-btn").ClickAsync();
            await page.WaitForFunctionAsync(
                $"() => {{ const r = [...document.querySelectorAll('#recv-body tr')].find(t => t.textContent.includes('{sender}')); return r && !r.querySelector('button.skip-btn'); }}",
                null, new PageWaitForFunctionOptions { Timeout = 15_000 });

            prompt.Should().Contain(streamId).And.Contain(sender).And.Contain("sn=1");
            (await page.EvaluateAsync<bool>("() => window.__pwned === undefined")).Should().BeTrue(
                "a stream id must never be executed as script");
            var cells = await page.Locator("#recv-body tr", new PageLocatorOptions { HasText = sender })
                .Locator("td").AllInnerTextsAsync();
            cells[2].Trim().Should().Be("4", "sn=3 was handed over, so sn=4 is next");
            cells[3].Trim().Should().Be("0");

            await page.GotoAsync($"{app.BaseUrl}/Messages?tab=dropped");
            var dropped = page.Locator("#dropped-body tr", new PageLocatorOptions { HasText = "skipped by operator" });
            await dropped.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        }
        finally
        {
            using var db = new SQLite.SQLiteConnection(app.DatabasePath);
            db.Execute("delete from streamrecvstate where Key = ?", key);
            db.Execute("delete from messages where Id = ?", id);
            db.Execute("delete from dropped_messages where StreamId = ?", streamId);
        }
    }
}
