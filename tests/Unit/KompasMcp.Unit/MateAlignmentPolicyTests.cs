using KompasMcp.Domain.Mates;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The mate-alignment geometry rule as a table, without KOMPAS.</summary>
/// <remarks>TEST: the pure rule the adapter judges a created mate by. INVARIANT: an explicit value is
/// confirmed by the measured face-normal dot (-1 for `opposite`, +1 for `cooriented`), `closest` is
/// never judged, and a value the type cannot express yields NO decision (which is a note, not a
/// refusal). The mismatch case is the negative control the acceptance order requires: it must refuse.
/// History: docs/decisions/mates.md#alignment-geometry-criterion</remarks>
public class MateAlignmentPolicyTests
{
    private const double Tolerance = 1e-3;

    [Theory]
    [InlineData("opposite", -1d)]
    [InlineData("cooriented", 1d)]
    [InlineData("closest", null)]
    [InlineData(null, null)]
    [InlineData("unknown", null)]
    public void OnlyExplicitValues_NameAnOrientation(string? requested, double? expected) =>
        Assert.Equal(expected, MateAlignmentPolicy.ExpectedDot(requested));

    [Theory]
    [InlineData("perpendicular", false)]
    [InlineData("coincidence", true)]
    [InlineData("parallel", true)]
    [InlineData("distance", true)]
    [InlineData("angle", true)]
    [InlineData(null, true)]
    public void OnlyPerpendicular_MakesTheDotUninformative(string? constraintType, bool confirms) =>
        Assert.Equal(confirms, MateAlignmentPolicy.DotConfirmsValue(constraintType));

    [Theory]
    // A dot that lands where the word says is confirmed.
    [InlineData("coincidence", "opposite", -1d, true)]
    [InlineData("coincidence", "cooriented", 1d, true)]
    [InlineData("parallel", "opposite", -1d, true)]
    [InlineData("distance", "cooriented", 1d, true)]
    // An unmeasured type is not an unconfirmed one: its faces still have a single normal.
    [InlineData("angle", "opposite", -1d, true)]
    // The NEGATIVE CONTROL: the geometry landed the other way — this is the refusal.
    [InlineData("coincidence", "opposite", 1d, false)]
    [InlineData("coincidence", "cooriented", -1d, false)]
    [InlineData("parallel", "opposite", 1d, false)]
    [InlineData("distance", "cooriented", -1d, false)]
    [InlineData("angle", "opposite", 0d, false)]
    // `closest` names no orientation: no decision either way, so it is never refused.
    [InlineData("coincidence", "closest", -1d, null)]
    [InlineData("coincidence", "closest", 1d, null)]
    [InlineData("parallel", "closest", null, null)]
    // A perpendicular mate cannot express the value through the dot: no decision, hence a note.
    [InlineData("perpendicular", "opposite", 0d, null)]
    [InlineData("perpendicular", "cooriented", 0d, null)]
    // Nothing requested, or the dot not read: nothing to decide.
    [InlineData("coincidence", null, -1d, null)]
    [InlineData("coincidence", "opposite", null, null)]
    public void DotAgainstExpectation_DecidesOnlyWhereGeometryCan(
        string? constraintType, string? requested, double? measured, bool? satisfied) =>
        Assert.Equal(satisfied,
            MateAlignmentPolicy.DotSatisfies(constraintType, requested, measured, Tolerance));

    [Theory]
    [InlineData(-1d, true)]
    [InlineData(-0.9995, true)]
    [InlineData(-1.0005, true)]
    [InlineData(-0.998, false)]
    [InlineData(-1.002, false)]
    public void ToleranceIsTheBoundaryOfTheDecision(double measured, bool satisfied) =>
        Assert.Equal(satisfied,
            MateAlignmentPolicy.DotSatisfies("coincidence", "opposite", measured, Tolerance));

    [Theory]
    [InlineData(1d, true)]
    [InlineData(0.9995, true)]
    [InlineData(0.998, false)]
    public void ToleranceAppliesToTheCoorientedSideToo(double measured, bool satisfied) =>
        Assert.Equal(satisfied,
            MateAlignmentPolicy.DotSatisfies("coincidence", "cooriented", measured, Tolerance));
}
