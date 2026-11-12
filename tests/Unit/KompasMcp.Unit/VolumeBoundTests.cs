using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The one rule that turns a measured material gain and the analytic prism into a verdict. The
/// prism (profile area × depth) is an UPPER BOUND: a gain inside it is NOT a failure, and a gain above it,
/// a zero gain and a gain of the wrong sign stay failures.</summary>
/// <remarks>The decision is a pure function of two numbers, so every branch is exercised without a CAD
/// session; the numbers are the ones the extrusion group measures (plate 100×80×10, disc R5×5).
/// History: docs/decisions/tests.md#volume-bound</remarks>
public class VolumeBoundTests
{
    private const double Prism = 392.69908169872417d; // π·5²·5, the disc the extrusion group cuts.

    [Fact]
    public void UnknownPrism_IsNoBound_AndNeverAVerdict()
    {
        foreach (var bound in new double?[] { null, 0d, -1d, double.NaN, double.PositiveInfinity })
        {
            var outcome = VolumeBound.Evaluate(100d, bound);

            Assert.Equal(VolumeBoundVerdict.NoBound, outcome.Verdict);
            Assert.Null(outcome.BoundMm3);
            Assert.False(outcome.IsConfirmed);
            Assert.False(outcome.IsWithinBound);
            Assert.False(outcome.IsOutsideBound);
        }
    }

    [Fact]
    public void UnreadGain_IsUnread_NotZeroAndNotAVerdict()
    {
        var outcome = VolumeBound.Evaluate(null, Prism);

        Assert.Equal(VolumeBoundVerdict.Unread, outcome.Verdict);
        Assert.Null(outcome.MeasuredMm3);
        Assert.Equal(Prism, outcome.BoundMm3!.Value);
        Assert.False(outcome.IsConfirmed);
        Assert.False(outcome.IsOutsideBound);
    }

    [Fact]
    public void GainEqualToThePrism_IsConfirmed()
    {
        var outcome = VolumeBound.Evaluate(Prism, Prism);

        Assert.Equal(VolumeBoundVerdict.Confirmed, outcome.Verdict);
        Assert.True(outcome.IsConfirmed);
        Assert.Equal(0d, outcome.DifferenceMm3!.Value);
    }

    [Fact]
    public void GainInsideTheToleranceOfThePrism_IsStillConfirmed()
    {
        // The tolerance is max(1e-3; 1e-9·|V|) — the same one every volume comparison in the project uses.
        var outcome = VolumeBound.Evaluate(Prism - (ProfileArea.Tolerance(Prism) / 2d), Prism);

        Assert.Equal(VolumeBoundVerdict.Confirmed, outcome.Verdict);
    }

    [Fact]
    public void GainBelowThePrism_IsWithinBound_NotAFailure()
    {
        // MEASURED shape of the defect: an overlapping boss adds less than the prism, and a cut through a
        // cavity removes less. Both are CORRECT operations and must not be reported as a failed check.
        var outcome = VolumeBound.Evaluate(162112.2958d, 291030.8602d);

        Assert.Equal(VolumeBoundVerdict.WithinBound, outcome.Verdict);
        Assert.True(outcome.IsWithinBound);
        Assert.False(outcome.IsOutsideBound);
        Assert.NotNull(outcome.Reason);
        Assert.StartsWith(VolumeBound.NotComputedCode, outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("ВЕРХНЯЯ граница", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("162112.2958", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("291030.8602", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GainAboveThePrism_IsOutsideBound()
    {
        var outcome = VolumeBound.Evaluate(Prism + 100d, Prism);

        Assert.Equal(VolumeBoundVerdict.OutsideBound, outcome.Verdict);
        Assert.True(outcome.IsOutsideBound);
        Assert.False(outcome.IsWithinBound);
        Assert.StartsWith(VolumeBound.OutsideCode, outcome.Reason!, StringComparison.Ordinal);
        Assert.Contains("больше призмы, ноль или знак не тот", outcome.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroGain_IsOutsideBound()
    {
        // A no-op is a failure of this check even though the bound is not exceeded: nothing happened.
        var outcome = VolumeBound.Evaluate(0d, Prism);

        Assert.Equal(VolumeBoundVerdict.OutsideBound, outcome.Verdict);
    }

    [Fact]
    public void GainOfTheWrongSign_IsOutsideBound()
    {
        // A boss that SHRANK the body: the material moved against the declared side.
        var outcome = VolumeBound.Evaluate(-50d, Prism);

        Assert.Equal(VolumeBoundVerdict.OutsideBound, outcome.Verdict);
    }

    [Fact]
    public void Difference_IsMeasuredMinusThePrism_AndNotReadWhenEitherSideIsMissing()
    {
        Assert.Equal(-1000d, VolumeBound.Evaluate(Prism - 1000d, Prism).DifferenceMm3!.Value);
        Assert.Null(VolumeBound.Evaluate(null, Prism).DifferenceMm3);
        Assert.Null(VolumeBound.Evaluate(Prism, null).DifferenceMm3);
        Assert.Equal("не читается", VolumeBound.Difference(VolumeBound.Evaluate(null, Prism)));
    }

    /// <summary>A symmetric extrusion is two-sided: the prism is area × depth × 2, and every other side is
    /// one-sided. MEASURED on the customer's grooved core: a symmetric cut of depth 0.9 removed 9,5448 mm³
    /// against a one-sided prism of 4,8099 mm³ — the one-sided figure called a correct cut "material from
    /// nowhere".</summary>
    [Fact]
    public void SymmetricExtrusion_DoublesThePrism_BecauseTheDepthIsPerSide()
    {
        var area = 5.3443d;
        var oneSided = VolumeBound.PrismMm3(area, 0.9d, ExtrudeDirection.Positive);

        Assert.Equal(area * 0.9d, oneSided, 9);
        Assert.Equal(2d * area * 0.9d, VolumeBound.PrismMm3(area, 0.9d, ExtrudeDirection.Symmetric), 9);
        Assert.Equal(oneSided, VolumeBound.PrismMm3(area, 0.9d, ExtrudeDirection.Negative));
    }

    /// <summary>The verdict the defect produced, and the verdict the doubled prism produces: the same
    /// measurement is "outside the bound" one-sided and "inside the bound" two-sided.</summary>
    [Fact]
    public void SymmetricBound_ContainsTheMeasuredRemoval_ThatTheOneSidedBoundCalledOutside()
    {
        var area = 5.3443d;
        var measured = 9.5448d;

        Assert.True(VolumeBound.Evaluate(measured, VolumeBound.PrismMm3(area, 0.9d, ExtrudeDirection.Positive))
            .IsOutsideBound);
        Assert.True(VolumeBound.Evaluate(measured, VolumeBound.PrismMm3(area, 0.9d, ExtrudeDirection.Symmetric))
            .IsWithinBound);
    }

    /// <summary>The check's message names BOTH sides for a symmetric sweep, so a correct cut cannot be told
    /// "the prism is 4,8099" as if that were the expectation.</summary>
    [Fact]
    public void SymmetricPrism_IsNamedWithBothSidesInTheReason()
    {
        var words = VolumeBound.PrismWords(ExtrudeDirection.Symmetric);
        var reason = VolumeBound.Evaluate(9.5448d, 9.6198d, words).Reason!;

        Assert.Contains("× 2", words);
        Assert.Contains("КАЖДУЮ сторону", words);
        Assert.Contains("× 2", reason);
        Assert.Contains("9.6198", reason);
        Assert.Equal("площадь × глубина", VolumeBound.PrismWords(ExtrudeDirection.Positive));
    }
}
