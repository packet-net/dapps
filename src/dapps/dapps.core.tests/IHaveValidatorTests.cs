using System.Text;
using dapps.client;
using dapps.core.Services;
using AwesomeAssertions;

namespace dapps.core.tests;

public class IHaveValidatorTests
{
    [Fact]
    public void Validate_MinimalLine_Succeeds()
    {
        var result = IHaveValidator.Validate("ihave 7b502c3 len=11 dst=app@gb7aaa-4");

        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.Id.Should().Be("7b502c3");
        result.Offer.Length.Should().Be(11);
        result.Offer.Format.Should().Be("p");                   // default
        result.Offer.Salt.Should().BeNull();
        result.Offer.CompressedLength.Should().BeNull();
        result.Offer.Destination.Should().Be("app@gb7aaa-4");
        result.Offer.AdditionalHeaders.Should().BeEmpty();
    }

    [Fact]
    public void Validate_FullReadmeExample_Succeeds()
    {
        var line = "ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value chk=6907";
        var result = IHaveValidator.Validate(line);

        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.Id.Should().Be("abcdeff");
        result.Offer.Length.Should().Be(11);
        result.Offer.Format.Should().Be("p");
        result.Offer.Salt.Should().Be(12345678L);
        result.Offer.Destination.Should().Be("appname@gb7aaa-4");
        result.Offer.Ttl.Should().Be(86400);
        result.Offer.AdditionalHeaders.Keys.Should().BeEquivalentTo(["key"]);
        result.Offer.AdditionalHeaders["key"].Should().Be("value");
    }

    [Theory]
    [InlineData("not even an ihave line")]
    [InlineData("ihave")]
    [InlineData("foo abcdeff len=11 dst=x@y")]
    public void Validate_NotIHaveCommand_Fails(string line)
        => IHaveValidator.Validate(line).IsValid.Should().BeFalse();

    [Fact]
    public void Validate_MissingLen_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("len");
    }

    [Fact]
    public void Validate_MissingDst_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc len=5");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("dst");
    }

    [Theory]
    [InlineData("len=foo")]
    [InlineData("len=-1")]
    public void Validate_BadLen_Fails(string lenToken)
    {
        var result = IHaveValidator.Validate($"ihave abc {lenToken} dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("len");
    }

    [Fact]
    public void Validate_BadFmt_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 fmt=q dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("fmt");
    }

    [Fact]
    public void Validate_FmtDWithoutClen_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 fmt=d dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("clen");
    }

    [Fact]
    public void Validate_FmtPWithClen_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 fmt=p clen=3 dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("clen");
    }

    [Fact]
    public void Validate_FmtAbsentWithClen_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 clen=3 dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("clen");
    }

    [Fact]
    public void Validate_FmtDWithClen_Succeeds()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 fmt=d clen=3 dst=app@x");
        result.IsValid.Should().BeTrue();
        result.Offer!.Format.Should().Be("d");
        result.Offer.CompressedLength.Should().Be(3);
    }

    [Fact]
    public void Validate_FmtZ1WithClen_Succeeds()
    {
        var result = IHaveValidator.Validate("ihave abc len=197 fmt=z1 clen=67 dst=app@x");
        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.Format.Should().Be("z1");
        result.Offer.CompressedLength.Should().Be(67);
    }

    [Fact]
    public void Validate_FmtZ1WithoutClen_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc len=197 fmt=z1 dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("clen");
    }

    [Fact]
    public void Validate_ADictionaryThisBuildDoesntHold_Fails()
    {
        // The sender reads the refusal and offers the message plain.
        var result = IHaveValidator.Validate("ihave abc len=197 fmt=z9 clen=67 dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("fmt=z9");
    }

    [Theory]
    [InlineData("clen=foo")]
    [InlineData("clen=-1")]
    public void Validate_BadClen_Fails(string clenToken)
    {
        var result = IHaveValidator.Validate($"ihave abc len=5 fmt=d {clenToken} dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("clen");
    }

    [Fact]
    public void Validate_BadSalt_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 s=foo dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("s=");
    }

    [Theory]
    [InlineData("brokentoken")]   // no `=`
    [InlineData("=value")]        // empty key
    [InlineData("key=")]          // empty value
    public void Validate_MalformedToken_Fails(string token)
    {
        var result = IHaveValidator.Validate($"ihave abc len=5 dst=app@x {token}");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ChkWithCorrectValue_Succeeds()
    {
        // Construct a valid line and compute its real chk.
        var inner = "ihave abc len=5 dst=app@x";
        var chk = IHaveCommand.Checksum(inner);
        var line = $"{inner} chk={chk}";

        IHaveValidator.Validate(line).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ChkWithWrongValue_FailsWithMismatch()
    {
        // The real chk for the inner line ≠ ffff
        var result = IHaveValidator.Validate("ihave abc len=5 dst=app@x chk=ffff");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("mismatch");
    }

    [Fact]
    public void Validate_ChkInMiddleOfLine_Fails()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 chk=0000 dst=app@x");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("last KV");
    }

    [Theory]
    [InlineData("chk=12")]        // too short
    [InlineData("chk=12345")]     // too long (and would also fail position rule, but value-shape check comes first)
    [InlineData("chk=zzzz")]      // not hex
    public void Validate_ChkNotFourHexChars_Fails(string chkToken)
    {
        var result = IHaveValidator.Validate($"ihave abc len=5 dst=app@x {chkToken}");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("4 hex");
    }

    [Fact]
    public void Validate_HeadersOmitsReservedKeys()
    {
        var line = "ihave abc len=5 fmt=p s=42 dst=app@x ttl=3600 priority=high";
        var result = IHaveValidator.Validate(line);

        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.AdditionalHeaders.Keys.Should().BeEquivalentTo(["priority"]);
        result.Offer.Ttl.Should().Be(3600);
    }

    [Fact]
    public void Validate_TtlAbsent_ParsesAsNull()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 dst=app@x");
        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.Ttl.Should().BeNull();
    }

    [Fact]
    public void Validate_TtlPresent_ParsesAsInt()
    {
        var result = IHaveValidator.Validate("ihave abc len=5 dst=app@x ttl=120");
        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.Ttl.Should().Be(120);
    }

    [Theory]
    [InlineData("ttl=foo")]
    [InlineData("ttl=-1")]
    [InlineData("ttl=0")]
    public void Validate_BadTtl_Fails(string ttlToken)
    {
        var result = IHaveValidator.Validate($"ihave abc len=5 dst=app@x {ttlToken}");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("ttl");
    }
}
