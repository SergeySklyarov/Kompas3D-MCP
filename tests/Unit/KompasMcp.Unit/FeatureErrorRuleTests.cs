using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The one rule that turns a tree feature's own kernel error into a refusal.</summary>
/// <remarks>WHY. A write call that answered true can leave the feature in the tree carrying a non-zero
/// <c>objectError</c> with the body unchanged. The hole order closed that for a hole; every other
/// feature-creating or feature-editing mutation needs the same decision, and it is one rule.
/// History: docs/decisions/adapter-features.md#feature-error-refusal</remarks>
public class FeatureErrorRuleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(-5)]
    public void NonZeroObjectError_IsARefusal(int code)
    {
        Assert.True(FeatureErrorRule.IsRefusal(code));
    }

    [Fact]
    public void ZeroObjectError_IsNotARefusal()
    {
        Assert.False(FeatureErrorRule.IsRefusal(0));
    }

    /// <summary>An unread state is a named gap: "we could not read it" must never become a refusal.</summary>
    [Fact]
    public void UnreadObjectError_IsNotARefusal()
    {
        Assert.False(FeatureErrorRule.IsRefusal(null));
    }

    /// <summary>The rule and the hole's own verdict must not disagree about the same input.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(null)]
    public void TheHolePolicyAgreesWithTheSharedRule(int? code)
    {
        var effect = HoleEffectPolicy.Evaluate(
            objectError: code, 100d, 100d, facesBefore: 6d, facesAfter: 6d, declaredMismatch: false);

        Assert.Equal(
            FeatureErrorRule.IsRefusal(code),
            effect.Verdict == HoleEffectVerdict.FeatureError);
    }
}
