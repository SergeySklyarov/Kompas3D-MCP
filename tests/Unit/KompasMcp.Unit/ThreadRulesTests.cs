using KompasMcp.Contracts;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The thread contract's pure rules: one base form, borders as a pair, a value OR its auto flag.</summary>
/// <remarks>DOC: <c>ithread_propers.html</c>, <c>ithread_autodiameter.html</c>, <c>ithread_autolenght.html</c> —
/// with auto-detection the product ignores the value the caller names, so accepting both would report a
/// number the model does not hold. History: docs/decisions/adapter-features.md#thread-route</remarks>
public sealed class ThreadRulesTests
{
    [Theory]
    [InlineData("edge", null, true)]
    [InlineData(null, "face", true)]
    [InlineData(null, null, false)]
    [InlineData("edge", "face", false)]
    public void Base_IsExactlyOneForm(string? edge, string? face, bool expected) =>
        Assert.Equal(expected, ThreadRules.SingleBase(edge, face));

    [Theory]
    [InlineData("top", "bottom", true)]
    [InlineData(null, null, true)]
    [InlineData("top", null, false)]
    [InlineData(null, "bottom", false)]
    public void Borders_AreAPair(string? initial, string? final, bool expected) =>
        Assert.Equal(expected, ThreadRules.BordersComplete(initial, final));

    [Theory]
    [InlineData(20d, true)]
    [InlineData(0.5d, true)]
    [InlineData(0d, false)]
    [InlineData(-2d, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Value_IsPositiveAndFinite(double? value, bool expected) =>
        Assert.Equal(expected, ThreadRules.PositiveFinite(value));

    // A value with its auto flag is refused: the product does not use the value then.
    [Theory]
    [InlineData(20d, false, true)]
    [InlineData(null, true, true)]
    [InlineData(20d, true, false)]
    [InlineData(null, false, false)]
    [InlineData(0d, false, false)]
    public void Value_OrAuto_NeverBoth(double? value, bool auto, bool expected) =>
        Assert.Equal(expected, ThreadRules.ValueOrAuto(value, auto));
}
