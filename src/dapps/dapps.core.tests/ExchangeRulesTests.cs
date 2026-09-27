using AwesomeAssertions;
using dapps.client.Backhaul;

namespace dapps.core.tests;

/// <summary>The <c>exchange</c> line: what a node says it takes.</summary>
public sealed class ExchangeRulesTests
{
    [Fact]
    public void ALine_RoundTrips()
    {
        var rules = new ExchangeRules("ab12cd", HoldSeconds: 120, Inline: 256, MaxBytes: 4096, Dictionaries: [1, 2]);

        var line = rules.ToLine();

        line.Should().Be("exchange id=ab12cd hold=120 inline=256 max=4096 z=1,2\n");
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

    [Fact]
    public void NewTags_AreShortHex_AndDiffer()
    {
        var tags = Enumerable.Range(0, 20).Select(_ => ExchangeRules.NewTag()).ToList();

        tags.Should().AllSatisfy(t => t.Should().MatchRegex("^[0-9a-f]{6}$"));
        tags.Distinct().Count().Should().BeGreaterThan(15);
    }
}
