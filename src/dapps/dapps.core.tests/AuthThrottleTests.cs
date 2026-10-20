using System.Net;
using AwesomeAssertions;
using dapps.core.Services;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

public sealed class AuthThrottleTests
{
    private static readonly IPAddress A = IPAddress.Parse("198.51.100.1");
    private static readonly IPAddress B = IPAddress.Parse("198.51.100.2");

    private static (AuthThrottle t, FakeTimeProvider clock) New()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        return (new AuthThrottle(clock), clock);
    }

    [Fact]
    public void FreeFailuresThenLockoutThatGrowsAndIsCapped()
    {
        var (t, clock) = New();
        for (var i = 0; i < AuthThrottle.FreeFailures; i++) t.Failed("login", A);
        t.LockedFor("login", A).Should().BeNull("the free tries are free");

        t.Failed("login", A);
        t.LockedFor("login", A).Should().Be(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(3));
        t.LockedFor("login", A).Should().BeNull("a short lockout ends by itself");

        for (var i = 0; i < 20; i++) { t.Failed("login", A); }
        t.LockedFor("login", A)!.Value.Should().BeLessThanOrEqualTo(AuthThrottle.MaxLockout);
    }

    [Fact]
    public void ScopesAndAddressesAreIndependent_AndSuccessClears()
    {
        var (t, _) = New();
        for (var i = 0; i < 8; i++) t.Failed("login", A);
        t.LockedFor("login", A).Should().NotBeNull();
        t.LockedFor("login", B).Should().BeNull();
        t.LockedFor("mqtt", A).Should().BeNull();

        t.Succeeded("login", A);
        t.LockedFor("login", A).Should().BeNull();
    }

    [Fact]
    public void FailuresAreForgottenAfterAQuietSpell()
    {
        var (t, clock) = New();
        for (var i = 0; i < 5; i++) t.Failed("rest", A);
        clock.Advance(AuthThrottle.Forget + TimeSpan.FromSeconds(1));
        t.Failed("rest", A);
        t.LockedFor("rest", A).Should().BeNull("the count started again from one");
    }

    [Fact]
    public void LoopbackAndUnknownAddressesAreNeverLocked()
    {
        var (t, _) = New();
        for (var i = 0; i < 50; i++) { t.Failed("mqtt", IPAddress.Loopback); t.Failed("mqtt", IPAddress.IPv6Loopback); t.Failed("mqtt", null); }
        t.LockedFor("mqtt", IPAddress.Loopback).Should().BeNull();
        t.LockedFor("mqtt", null).Should().BeNull();
    }

    [Fact]
    public void MqttEndpointStringsParse()
    {
        AuthThrottle.AddressOf("192.0.2.5:5555").Should().Be(IPAddress.Parse("192.0.2.5"));
        AuthThrottle.AddressOf("[2001:db8::1]:1883").Should().Be(IPAddress.Parse("2001:db8::1"));
        AuthThrottle.AddressOf("garbage").Should().BeNull();
    }
}
