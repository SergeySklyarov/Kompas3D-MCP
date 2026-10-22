using KompasMcp.Contracts;
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

    [Theory]
    // A parametric mate takes its parameter and nothing of the transmission.
    [InlineData(false, 10d, null, null, null, null, MateTransmissionPolicy.EditRefusal.None)]
    [InlineData(false, null, null, null, null, null, MateTransmissionPolicy.EditRefusal.NoEditFields)]
    [InlineData(false, 10d, 1d, 2d, null, null, MateTransmissionPolicy.EditRefusal.TransmissionFieldOnParamEdit)]
    [InlineData(false, null, null, null, true, null, MateTransmissionPolicy.EditRefusal.TransmissionFieldOnParamEdit)]
    // A transmission takes its ratio and directions, never a parameter, and the ratio is a pair.
    [InlineData(true, 10d, 1d, 2d, null, null, MateTransmissionPolicy.EditRefusal.ParamValueOnTransmission)]
    [InlineData(true, null, 1d, null, null, null, MateTransmissionPolicy.EditRefusal.IncompleteRatio)]
    [InlineData(true, null, null, 2d, null, null, MateTransmissionPolicy.EditRefusal.IncompleteRatio)]
    [InlineData(true, null, 0d, 2d, null, null, MateTransmissionPolicy.EditRefusal.RatioOutOfRule)]
    [InlineData(true, null, 1d, -2d, null, null, MateTransmissionPolicy.EditRefusal.RatioOutOfRule)]
    [InlineData(true, null, double.NaN, 2d, null, null, MateTransmissionPolicy.EditRefusal.RatioOutOfRule)]
    [InlineData(true, null, 1d, 4d, null, null, MateTransmissionPolicy.EditRefusal.None)]
    [InlineData(true, null, null, null, false, null, MateTransmissionPolicy.EditRefusal.None)]
    [InlineData(true, null, 1d, 4d, true, false, MateTransmissionPolicy.EditRefusal.None)]
    [InlineData(true, null, null, null, null, null, MateTransmissionPolicy.EditRefusal.NoEditFields)]
    public void EditFields_FollowTheMateType(
        bool isTransmission, double? paramValue, double? scale1, double? scale2,
        bool? direction1, bool? direction2, MateTransmissionPolicy.EditRefusal expected) =>
        Assert.Equal(expected, MateTransmissionPolicy.CheckEdit(
            isTransmission, paramValue, scale1, scale2, direction1, direction2));

    [Fact]
    public void Edit_ReadBackAsRequested_IsConfirmed()
    {
        var read = new MateTransmissionStateDto
        {
            Scale1 = 1d, Scale2 = 4d, Direction1 = true, Direction2 = false, Valid = true,
        };

        Assert.Empty(MateTransmissionPolicy.EditMismatches(read, 1d, 4d, true, false));
    }

    [Fact]
    public void Edit_ReadBackDiffers_IsNotConfirmed()
    {
        var read = new MateTransmissionStateDto
        {
            Scale1 = 1d, Scale2 = 2d, Direction1 = true, Direction2 = true, Valid = true,
        };

        Assert.Equal(new[] { "scale" }, MateTransmissionPolicy.EditMismatches(read, 1d, 4d, null, null));
        Assert.Equal(new[] { "direction2" }, MateTransmissionPolicy.EditMismatches(read, null, null, null, false));
    }

    [Fact]
    public void Edit_NotRead_IsNotConfirmed()
    {
        var requested = MateTransmissionPolicy.EditMismatches(
            new MateTransmissionStateDto(), 1d, 4d, true, false);

        Assert.Equal(new[] { "scale", "direction1", "direction2" }, requested);
        Assert.Equal(3, MateTransmissionPolicy.EditMismatches(null, 1d, 4d, true, false).Count);
    }
}
