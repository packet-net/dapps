using AwesomeAssertions;
using dapps.client.Backhaul;

namespace dapps.core.tests;

/// <summary>The <c>exchange</c> line: what a node says it takes.</summary>
public sealed class ExchangeRulesTests
{
    [Fact]
    public void ALine_RoundTrips()
    {
        var rules = new ExchangeRules("ab12cd", HoldSeconds: 120, Inline: 256, MaxBytes: 4096, Dictionaries: [1, 2], Node: "G6FCI-8");

        var line = rules.ToLine();

        line.Should().Be("exchange id=ab12cd hold=120 inline=256 max=4096 z=1,2 node=G6FCI-8\n");
        ExchangeRules.Parse(line.TrimEnd('\n')).Should().BeEquivalentTo(rules);
    }

    [Fact]
    public void WhatALineLeavesOut_AsksTheLeastOfTheOtherEnd()
    {
        var rules = ExchangeRules.Parse("exchange");

        rules.Tag.Should().BeEmpty();
        rules.HoldSeconds.Should().Be(0, "no hold");
        rules.Inline.Should().Be(0, "offer everything first");
        rules.MaxBytes.Should().BeNull("no limit");
        rules.Dictionaries.Should().BeEmpty("plain only");
        rules.Node.Should().BeNull("an old peer is identified by the AX.25 source");
    }

    [Fact]
    public void UnknownKeysAndValuesThatDontParse_AreIgnored()
    {
        var rules = ExchangeRules.Parse("exchange id=x1 hold=soon inline=-5 max=big z=1,two future=yes");

        rules.Tag.Should().Be("x1");
        rules.HoldSeconds.Should().Be(0);
        rules.Inline.Should().Be(0);
        rules.MaxBytes.Should().BeNull();
        rules.Dictionaries.Should().Equal(1);
    }

    [Theory]
    [InlineData("gb7lox", "GB7LOX")]
    [InlineData("G6FCI-8", "G6FCI-8")]
    [InlineData("GB7LOX-0", "GB7LOX-0")]
    [InlineData("", null)]
    [InlineData("GB7LOX-16", null)]
    [InlineData("GB7LOX-06", null)]
    [InlineData("GB7LOX-6-7", null)]
    [InlineData("GB7LOX\tquit", null)]
    public void NodeIdentity_RequiresAValidCallsign(string value, string? expected)
    {
        ExchangeRules.Parse($"exchange id=old01 node={value} inline=256").Node.Should().Be(expected);
    }

    [Theory]
    [InlineData("GB7LOX\nquit")]
    [InlineData("GB7LOX node=G6FCI-8")]
    [InlineData("GB7LOX-16")]
    [InlineData("")]
    [InlineData(null)]
    public void AnInvalidOwnNodeIdentity_IsOmittedWithoutFailingTheExchange(string? node)
    {
        var rules = new ExchangeRules("own01", 0, 256, null, [], node);

        var line = rules.ToLine();

        line.Should().Be("exchange id=own01 hold=0 inline=256\n");
        ExchangeRules.Parse(line).Node.Should().BeNull();
    }

    [Fact]
    public void AnOldFormatLine_StillParsesAndSerialisesWithoutNodeIdentity()
    {
        const string line = "exchange id=old01 hold=60 inline=256 z=1";
        var rules = ExchangeRules.Parse(line);

        rules.Node.Should().BeNull();
        rules.ToLine().Should().Be(line + "\n");
    }

    [Fact]
    public void NewTags_AreShortHex_AndDiffer()
    {
        var tags = Enumerable.Range(0, 20).Select(_ => ExchangeRules.NewTag()).ToList();

        tags.Should().AllSatisfy(t => t.Should().MatchRegex("^[0-9a-f]{6}$"));
        tags.Distinct().Count().Should().BeGreaterThan(15);
    }
}
