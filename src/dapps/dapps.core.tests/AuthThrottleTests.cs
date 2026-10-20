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

    private static void Fail(AuthThrottle t, string scope, IPAddress a, int times)
    {
        for (var i = 0; i < times; i++) t.Begin(scope, a);
    }

    [Fact]
    public void FreeFailuresThenLockoutThatGrowsAndIsCapped()
    {
        var (t, clock) = New();
        Fail(t, "login", A, AuthThrottle.FreeFailures);
        t.Begin("login", A).Should().BeNull("the sixth try is still let through...");
        t.Begin("login", A).Should().Be(TimeSpan.FromSeconds(2), "...and after it the address waits");
        clock.Advance(TimeSpan.FromSeconds(3));
        t.Begin("login", A).Should().BeNull("a short lockout ends by itself");

        for (var i = 0; i < 20; i++) { clock.Advance(AuthThrottle.MaxLockout + TimeSpan.FromSeconds(1)); t.Begin("login", A); }
        clock.Advance(TimeSpan.FromSeconds(1));
        t.Begin("login", A);
        t.Begin("login", A)!.Value.Should().BeLessThanOrEqualTo(AuthThrottle.MaxLockout);
    }

    [Fact]
    public void ParallelAttempts_AreCountedBeforeTheyAreChecked()
    {
        var (t, _) = New();
        var allowed = 0;
        Parallel.For(0, 200, _ =>
        {
            if (t.Begin("login", A) is null) Interlocked.Increment(ref allowed);
        });
        allowed.Should().Be(AuthThrottle.FreeFailures + 1,
            "only the free tries (plus the one that trips the lock) may reach the slow hash, however many arrive at once");
    }

    [Fact]
    public void ScopesAndAddressesAreIndependent_AndLoginSuccessClears()
    {
        var (t, _) = New();
        Fail(t, "login", A, 8);
        t.Begin("login", A).Should().NotBeNull();
        t.Begin("login", B).Should().BeNull();
        t.Begin("mqtt", A).Should().BeNull();

        t.Succeeded("login", A, clearAll: true);
        t.Begin("login", A).Should().BeNull();
    }

    [Fact]
    public void TokenSuccessOnlyTakesBackItsOwnAttempt()
    {
        var (t, _) = New();
        Fail(t, "rest", A, 5);               // five wrong guesses
        t.Begin("rest", A).Should().BeNull(); // sixth attempt is a valid token
        t.Succeeded("rest", A);               // gives back just that one
        t.Begin("rest", A).Should().BeNull("five failures stand, so the next try is the sixth again");
        t.Begin("rest", A).Should().NotBeNull("a valid token does not wipe the guesses");
    }

    [Fact]
    public void FailuresAreForgottenAfterAQuietSpell()
    {
        var (t, clock) = New();
        Fail(t, "rest", A, 5);
        clock.Advance(AuthThrottle.Forget + TimeSpan.FromSeconds(1));
        t.Begin("rest", A).Should().BeNull();
        t.Begin("rest", A).Should().BeNull("the count started again from one");
    }

    [Fact]
    public void Ipv6IsKeyedBySlash64()
    {
        var (t, _) = New();
        for (var i = 0; i < 8; i++) t.Begin("login", IPAddress.Parse("2001:db8:1:2::" + (i + 1)));
        t.Begin("login", IPAddress.Parse("2001:db8:1:2:aaaa::9")).Should().NotBeNull();
        t.Begin("login", IPAddress.Parse("2001:db8:1:3::1")).Should().BeNull();
    }

    [Fact]
    public void LoopbackAndUnknownAddressesAreNeverLocked()
    {
        var (t, _) = New();
        for (var i = 0; i < 50; i++) { t.Begin("mqtt", IPAddress.Loopback); t.Begin("mqtt", IPAddress.IPv6Loopback); t.Begin("mqtt", null); }
        t.Begin("mqtt", IPAddress.Loopback).Should().BeNull();
        t.Begin("mqtt", null).Should().BeNull();
    }

    [Fact]
    public void MqttEndpointStringsParse()
    {
        AuthThrottle.AddressOf("192.0.2.5:5555").Should().Be(IPAddress.Parse("192.0.2.5"));
        AuthThrottle.AddressOf("[2001:db8::1]:1883").Should().Be(IPAddress.Parse("2001:db8::1"));
        AuthThrottle.AddressOf("garbage").Should().BeNull();
    }
}
