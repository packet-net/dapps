using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using dapps.meshcore;
using Xunit;

namespace dapps.core.tests;

/// <summary>
/// Deployment models B/C (#24): the bearer exposes preset + flood-scope as first-class
/// config. Model A = unscoped public preset, B = scoped public preset (flood-scope key),
/// C = dedicated/custom preset (own freq/SF). These tests pin the config resolution -
/// the on-air behaviour of the scope key is validated by the soak.
/// </summary>
public sealed class MeshCoreDeploymentModelTests
{
    // ── Model C: custom dedicated preset ───────────────────────────

    [Fact]
    public void ParseCustom_ValidSpec_ProducesPreset()
    {
        var p = Regions.ParseCustom("freq=868.4;bw=62.5;sf=8;cr=8;pwr=14");
        p.Name.Should().Be(Regions.CustomName);
        p.FreqMhz.Should().Be(868.4);
        p.BwKhz.Should().Be(62.5);
        p.Sf.Should().Be(8);
        p.Cr.Should().Be(8);
        p.MaxPowerDbm.Should().Be(14);
    }

    [Fact]
    public void ParseCustom_ToleratesCommasAndWhitespace()
    {
        var p = Regions.ParseCustom(" freq=869.5 , bw=250 , sf=11 , cr=5 , pwr=27 ");
        p.FreqMhz.Should().Be(869.5);
        p.Sf.Should().Be(11);
        p.MaxPowerDbm.Should().Be(27);
    }

    [Theory]
    [InlineData("")]                                            // empty
    [InlineData("freq=868.4;bw=62.5;sf=8;cr=8")]                // missing pwr
    [InlineData("freq=868.4;bw=62.5;sf=99;cr=8;pwr=14")]        // sf out of range
    [InlineData("freq=868.4;bw=62.5;sf=8;cr=8;pwr=99")]         // pwr out of range
    [InlineData("freq=abc;bw=62.5;sf=8;cr=8;pwr=14")]           // freq not a number
    [InlineData("freq;bw=62.5;sf=8;cr=8;pwr=14")]               // malformed field
    public void ParseCustom_Rejects_BadSpecs(string spec)
    {
        var act = () => Regions.ParseCustom(spec);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResolveRegion_Custom_UsesCustomPreset()
    {
        var opts = new MeshCoreBearerOptions { Region = "custom", CustomPreset = "freq=867.1;bw=125;sf=9;cr=6;pwr=20" };
        var p = opts.ResolveRegion();
        p.FreqMhz.Should().Be(867.1);
        p.Sf.Should().Be(9);
    }

    [Fact]
    public void ResolveRegion_BakedName_StillWorks()
    {
        new MeshCoreBearerOptions { Region = "uk-test" }.ResolveRegion().FreqMhz.Should().Be(868.4);
    }

    // ── Model B: flood-scope key derivation ────────────────────────

    [Fact]
    public void ResolveFloodScopeKey_Empty_IsUnscoped()
    {
        new MeshCoreBearerOptions { FloodScopeKey = "" }.ResolveFloodScopeKey().Should().BeNull();
        new MeshCoreBearerOptions { FloodScopeKey = "   " }.ResolveFloodScopeKey().Should().BeNull();
    }

    [Fact]
    public void ResolveFloodScopeKey_RegionName_HashesLikeMeshCorePublicKey()
    {
        // MeshCore derives a public region key as SHA256("#"+name)[..16]; matching that
        // lets repeaters configured with `region put <name>` carry our scoped floods.
        var key = new MeshCoreBearerOptions { FloodScopeKey = "dapps-uk" }.ResolveFloodScopeKey();
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes("#dapps-uk"))[..16];
        key.Should().Equal(expected);
        key!.Length.Should().Be(16);
    }

    [Fact]
    public void ResolveFloodScopeKey_HexString_IsVerbatim()
    {
        var hex = "00112233445566778899aabbccddeeff";
        var key = new MeshCoreBearerOptions { FloodScopeKey = hex }.ResolveFloodScopeKey();
        Convert.ToHexString(key!).ToLowerInvariant().Should().Be(hex);
    }

    // ── DeploymentModel classification ─────────────────────────────

    [Theory]
    [InlineData("uk-narrow", "", "A")]     // unscoped public preset
    [InlineData("uk-narrow", "dapps-uk", "B")]  // scoped public preset
    [InlineData("custom", "", "C")]        // dedicated preset (scope irrelevant)
    [InlineData("custom", "dapps-uk", "C")]     // dedicated wins over scope in the label
    public void DeploymentModel_Classifies(string region, string scope, string expectedPrefix)
    {
        var opts = new MeshCoreBearerOptions { Region = region, FloodScopeKey = scope };
        opts.DeploymentModel().Should().StartWith(expectedPrefix);
    }
}
