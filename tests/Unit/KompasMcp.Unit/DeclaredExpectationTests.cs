using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The one rule that turns a client-declared expectation of a mutation's result into a verdict
/// (customer decision recorded in the decision doc). A declared expectation that did not hold is a
/// REFUSAL, not a lowered level; an expectation that could not be compared is a NAMED gap.</summary>
/// <remarks>The decision is a pure function of two numbers, so a SUBSTITUTED read result exercises every
/// branch without a CAD session.
/// History: docs/decisions/tests.md#declared-expectation-rule</remarks>
public class DeclaredExpectationTests
{
    [Fact]
    public void NoDeclaredExpectation_IsNeitherConfirmedNorRefused()
    {
        var outcome = DeclaredExpectation.Evaluate(null, 12345d);

        Assert.Equal(DeclaredExpectationVerdict.NotDeclared, outcome.Verdict);
        Assert.False(outcome.IsDeclared);
        Assert.False(outcome.IsRefusal);
        Assert.False(outcome.IsUnverifiable);
    }

    [Fact]
    public void DeclaredExpectation_ThatMatches_IsConfirmed()
    {
        var outcome = DeclaredExpectation.Evaluate(80000d, 80000d);

        Assert.Equal(DeclaredExpectationVerdict.Confirmed, outcome.Verdict);
        Assert.True(outcome.IsConfirmed);
        Assert.False(outcome.IsRefusal);
    }

    [Fact]
    public void DeclaredExpectation_ThatDoesNotMatch_IsARefusal_WithTheNumbers()
    {
        var outcome = DeclaredExpectation.Evaluate(80214.60183660255d, 79214.60183660254d);

        Assert.Equal(DeclaredExpectationVerdict.NotConfirmed, outcome.Verdict);
        Assert.True(outcome.IsRefusal);
        Assert.Equal(80214.60183660255d, outcome.ExpectedMm3);
        Assert.Equal(79214.60183660254d, outcome.MeasuredMm3);
        Assert.NotNull(outcome.DifferenceMm3);
        Assert.True(Math.Abs(outcome.DifferenceMm3!.Value - (-1000d)) < 1e-6);

        var refusal = DeclaredExpectation.Refusal(outcome, "kompas_test", "объём после операции", 11);
        Assert.Equal(ErrorCodes.GeometryFailed, refusal.Code);
        Assert.True(refusal.PartialEffects);
        Assert.Equal(DeclaredExpectation.NotConfirmedCode, refusal.Details!["code"]);
        Assert.Equal(80214.60183660255d, refusal.Details["expected_volume_mm3"]);
        Assert.Equal(79214.60183660254d, refusal.Details["measured_volume_mm3"]);
        Assert.Equal(11L, refusal.Details["revision_after"]);
    }

    [Fact]
    public void DeclaredExpectation_ThatCouldNotBeCompared_IsUnverifiable_NotARefusal()
    {
        var outcome = DeclaredExpectation.Evaluate(80000d, null);

        Assert.Equal(DeclaredExpectationVerdict.Unverifiable, outcome.Verdict);
        Assert.True(outcome.IsUnverifiable);
        Assert.False(outcome.IsRefusal);
        Assert.Contains("declared_expectation_unreadable", DeclaredExpectation.UnreadableReason("объём"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToleranceBoundary_IsInclusive()
    {
        // The tolerance is the shared profile tolerance: 1e-3 mm³ absolute or 1e-9 relative, the larger.
        // At the absolute floor the arithmetic is exact, so the inclusive comparison is asserted without
        // floating-point noise: exactly the tolerance passes, a hair beyond it does not.
        Assert.Equal(DeclaredExpectationVerdict.Confirmed, DeclaredExpectation.Evaluate(0d, 1e-3).Verdict);
        Assert.Equal(DeclaredExpectationVerdict.NotConfirmed, DeclaredExpectation.Evaluate(0d, 1.001e-3).Verdict);

        // Strictly inside and strictly outside on a real volume, where the boundary itself is not exact.
        var expected = 80000d;
        var tolerance = DeclaredExpectation.Tolerance(expected);
        Assert.Equal(DeclaredExpectationVerdict.Confirmed,
            DeclaredExpectation.Evaluate(expected, expected + (0.5 * tolerance)).Verdict);
        Assert.Equal(DeclaredExpectationVerdict.Confirmed,
            DeclaredExpectation.Evaluate(expected, expected - (0.5 * tolerance)).Verdict);
        Assert.Equal(DeclaredExpectationVerdict.NotConfirmed,
            DeclaredExpectation.Evaluate(expected, expected + (2 * tolerance)).Verdict);
    }

    [Fact]
    public void ASmallModelIsNotFailedByTheRelativeFloor_AndALargeOneIsNotPassedByIt()
    {
        // A tiny model must not be failed by floating-point noise, a huge one must not be passed by an
        // oversized absolute slack: the tolerance is the LARGER of the absolute floor and the relative part.
        Assert.Equal(1e-3d, DeclaredExpectation.Tolerance(1d));
        Assert.Equal(1e-9d * 1e9, DeclaredExpectation.Tolerance(1e9d));
    }
}
