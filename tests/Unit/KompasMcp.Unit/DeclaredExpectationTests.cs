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

    [Fact]
    public void AConfirmedFirstRead_DoesNotReReadTheVolume()
    {
        // The second read is a repeat of the MEASUREMENT and is only worth its cost when the first read
        // disagreed: a confirmed read must not pay for it (naryad PRE_RELEASE_0_6_0 П2.3).
        var rereadCalls = 0;

        var outcome = DeclaredExpectation.Evaluate(80000d, 80000d, () =>
        {
            rereadCalls++;
            return 80000d;
        });

        Assert.Equal(DeclaredExpectationVerdict.Confirmed, outcome.Verdict);
        Assert.Equal(0, rereadCalls);
    }

    [Fact]
    public void TwoReadsThatAgree_AndBothMissTheExpectation_AreARefusal()
    {
        // Both reads readable and equal, and neither matched: a genuine refusal with the numbers.
        var outcome = DeclaredExpectation.Evaluate(80214.60183660255d, 79214.60183660254d,
            () => 79214.60183660254d);

        Assert.Equal(DeclaredExpectationVerdict.NotConfirmed, outcome.Verdict);
        Assert.True(outcome.IsRefusal);
        Assert.Equal(79214.60183660254d, outcome.MeasuredMm3);
    }

    [Fact]
    public void TwoReadsThatDisagree_AreANamedGap_NotARefusal()
    {
        // MEASURED: the model state can read one operation behind. A second read that differs from the
        // first means the measurement is not repeatable, so neither number may be held against the
        // declared expectation — this is a NAMED gap, never a false refusal.
        var first = 79123.456789d;
        var second = 80321.987654d;

        var outcome = DeclaredExpectation.Evaluate(80000d, first, () => second);

        Assert.Equal(DeclaredExpectationVerdict.Unverifiable, outcome.Verdict);
        Assert.True(outcome.IsUnverifiable);
        Assert.False(outcome.IsRefusal);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("declared_expectation_reads_diverged", outcome.Reason!, StringComparison.Ordinal);
        Assert.Contains("79123.456789", outcome.Reason!, StringComparison.Ordinal);
        Assert.Contains("80321.987654", outcome.Reason!, StringComparison.Ordinal);

        // The published reason prefers the divergence text over the generic "could not be compared".
        var reason = DeclaredExpectation.UnverifiableReason("объём после правки", outcome);
        Assert.Contains("declared_expectation_reads_diverged", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondReadThatConfirms_TheExpectation_IsAConfirmation()
    {
        // The stale first read disagreed; the repeat of the MEASUREMENT confirmed the expectation.
        var outcome = DeclaredExpectation.Evaluate(127564.6153839594d, 128334.30559701854d,
            () => 127564.6153839594d);

        Assert.Equal(DeclaredExpectationVerdict.Confirmed, outcome.Verdict);
        Assert.True(outcome.IsConfirmed);
        Assert.Equal(127564.6153839594d, outcome.MeasuredMm3);
    }

    [Fact]
    public void AnUnreadableFirstRead_WithADifferentSecondRead_IsANamedGap()
    {
        // One readable and one unreadable read cannot be "in agreement", so the outcome is a named gap.
        var outcome = DeclaredExpectation.Evaluate(80000d, null, () => 90000d);

        Assert.Equal(DeclaredExpectationVerdict.Unverifiable, outcome.Verdict);
        Assert.False(outcome.IsRefusal);
        Assert.Contains("declared_expectation_reads_diverged", outcome.Reason!, StringComparison.Ordinal);
    }
}
