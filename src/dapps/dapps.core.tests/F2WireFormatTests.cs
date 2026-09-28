using AwesomeAssertions;
using dapps.client;

namespace dapps.core.tests;

/// <summary>
/// Plan F2 - parse + reject paths for the new <c>mid=</c> and
/// <c>frag=N/M</c> headers on <c>ihave</c>. Pre-F2 senders that omit
/// both must still parse successfully (backward compat); a sender
/// that supplies one without the other is malformed and must reject.
/// </summary>
public sealed class F2WireFormatTests
{
    [Fact]
    public void Validate_NoFragHeaders_ParsesAsSinglePart()
    {
        var result = IHaveValidator.Validate("ihave abc1234 len=11 fmt=p dst=app@N0CALL");
        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.MasterId.Should().BeNull();
        result.Offer.Fragment.Should().BeNull();
    }

    [Fact]
    public void Validate_FullFragHeaders_ParsesIndexAndTotal()
    {
        var result = IHaveValidator.Validate(
            "ihave abc1234 len=4096 fmt=p dst=app@N0CALL mid=def5678 frag=2/5");
        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.MasterId.Should().Be("def5678");
        result.Offer.Fragment.Should().Be(new FragmentInfo(2, 5));
    }

    [Fact]
    public void Validate_FragHeadersInAnyOrderRelativeToOthers_ParsesEqually()
    {
        // KV order in ihave is unrestricted; the parser must not depend
        // on mid/frag appearing in any particular position.
        var result = IHaveValidator.Validate(
            "ihave abc1234 frag=3/5 len=4096 mid=def5678 fmt=p dst=app@N0CALL");
        result.IsValid.Should().BeTrue($"got error: {result.Error}");
        result.Offer!.Fragment.Should().Be(new FragmentInfo(3, 5));
    }

    [Fact]
    public void Validate_MidWithoutFrag_Rejected()
    {
        var result = IHaveValidator.Validate(
            "ihave abc1234 len=11 fmt=p dst=app@N0CALL mid=def5678");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("mid= and frag= must both be present");
    }

    [Fact]
    public void Validate_FragWithoutMid_Rejected()
    {
        var result = IHaveValidator.Validate(
            "ihave abc1234 len=11 fmt=p dst=app@N0CALL frag=1/3");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("mid= and frag= must both be present");
    }

    [Fact]
    public void Validate_FragTotalIsOne_Rejected()
    {
        // A single-fragment message is just a normal message; the wire
        // shape must not include "frag=1/1" because it implies multi-
        // part. Reject defensively so a confused sender doesn't get a
        // free pass.
        var result = IHaveValidator.Validate(
            "ihave abc1234 len=11 fmt=p dst=app@N0CALL mid=def5678 frag=1/1");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("total must be at least 2");
    }

    [Fact]
    public void Validate_FragIndexOutOfRange_Rejected()
    {
        var result = IHaveValidator.Validate(
            "ihave abc1234 len=11 fmt=p dst=app@N0CALL mid=def5678 frag=4/3");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("1 <= N <= M");
    }

    [Fact]
    public void Validate_FragIndexZero_Rejected()
    {
        var result = IHaveValidator.Validate(
            "ihave abc1234 len=11 fmt=p dst=app@N0CALL mid=def5678 frag=0/3");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("1 <= N <= M");
    }

    [Fact]
    public void Validate_MalformedFragSyntax_Rejected()
    {
        var result = IHaveValidator.Validate(
            "ihave abc1234 len=11 fmt=p dst=app@N0CALL mid=def5678 frag=oneoftwo");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("frag= must be N/M");
    }

    [Fact]
    public void Validate_NonNumericFragParts_Rejected()
    {
        var result = IHaveValidator.Validate(
            "ihave abc1234 len=11 fmt=p dst=app@N0CALL mid=def5678 frag=a/b");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("non-negative integers");
    }
}
