using KompasMcp.Domain.Mates;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The transmission input rule as a table, without KOMPAS.</summary>
/// <remarks>TEST: the pure rule the adapter refuses a transmission input by, before <c>Add</c> creates a
/// mate. INVARIANT: the ratio is two finite numbers greater than zero, the motion is one this volume
/// implements, and "not read" is never "equal". The negative controls are the refusals MM-10 requires.
/// History: docs/decisions/mates.md#mechanical-transmission</remarks>
public class MateTransmissionPolicyTests
{
    [Theory]
    [InlineData("rotation", true)]
    [InlineData("linear", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyRotation_IsImplemented(string? motion, bool known) =>
        Assert.Equal(known, MateTransmissionPolicy.IsKnownMotion(motion));

    [Theory]
    [InlineData(1d, true)]
    [InlineData(0.5d, true)]
    [InlineData(0d, false)]
    [InlineData(-1d, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void RatioSide_IsFiniteAndPositive(double value, bool usable) =>
        Assert.Equal(usable, MateTransmissionPolicy.IsUsableScale(value));

    [Fact]
    public void RatioSide_NotSet_IsNotUsable() =>
        Assert.False(MateTransmissionPolicy.IsUsableScale(null));

    [Theory]
    // Read back exactly as written — the measured case.
    [InlineData(1d, 2d, 1d, 2d, true)]
    // The negative control: the model kept another ratio, so the call is not a success.
    [InlineData(1d, 2d, 1d, 3d, false)]
    [InlineData(1d, 2d, 2d, 1d, false)]
    public void Ratio_IsJudgedByTheReadBack(
        double read1, double read2, double requested1, double requested2, bool matches) =>
        Assert.Equal(matches, MateTransmissionPolicy.RatioMatches(read1, read2, requested1, requested2));

    [Fact]
    public void Ratio_NotRead_IsNotConfirmed() =>
        Assert.Null(MateTransmissionPolicy.RatioMatches(null, 2d, 1d, 2d));
}
