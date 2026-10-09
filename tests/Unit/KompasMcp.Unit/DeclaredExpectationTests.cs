using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The one rule that turns a client-declared expectation of a mutation's result into a verdict.
/// A declared expectation that did not hold is a FAILED CHECK with a warning and a lowered level, NOT a
/// refusal; an expectation that could not be compared is a NAMED gap.</summary>
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
        Assert.False(outcome.IsNotConfirmed);
        Assert.False(outcome.IsUnverifiable);
        Assert.Null(DeclaredExpectation.Warnings(outcome));
    }

    [Fact]
    public void DeclaredExpectation_ThatMatches_IsConfirmed()
    {
        var outcome = DeclaredExpectation.Evaluate(80000d, 80000d);

        Assert.Equal(DeclaredExpectationVerdict.Confirmed, outcome.Verdict);
        Assert.True(outcome.IsConfirmed);
        Assert.False(outcome.IsNotConfirmed);
    }

    [Fact]
    public void DeclaredExpectation_ThatDoesNotMatch_IsAFailedCheck_NotARefusal_WithTheNumbers()
    {
        var outcome = DeclaredExpectation.Evaluate(80214.60183660255d, 79214.60183660254d);

        Assert.Equal(DeclaredExpectationVerdict.NotConfirmed, outcome.Verdict);
        Assert.True(outcome.IsNotConfirmed);
        Assert.False(outcome.IsUnverifiable);
        Assert.Equal(80214.60183660255d, outcome.ExpectedMm3);
        Assert.Equal(79214.60183660254d, outcome.MeasuredMm3);
        Assert.NotNull(outcome.DifferenceMm3);
        Assert.True(Math.Abs(outcome.DifferenceMm3!.Value - (-1000d)) < 1e-6);

        // The check is NAMED and FAILED, and carries the declared, the measured, the difference and the
        // tolerance: a caller that reads one field must not have to re-derive the others. The numbers are
        // published through the same six-decimal formatter every other check uses.
        var check = DeclaredExpectation.Check(DeclaredExpectation.NotConfirmedCode, outcome);
        Assert.Equal(DeclaredExpectation.NotConfirmedCode, check.Name);
        Assert.False(check.Passed);
        Assert.Equal("80214.601837", check.Expected);
        Assert.Contains("79214.601837", check.Observed!, StringComparison.Ordinal);
        Assert.Contains("разность -1000", check.Observed!, StringComparison.Ordinal);
        Assert.Contains("допуск 0.001", check.Observed!, StringComparison.Ordinal);

        // The warning is published and names the three numbers.
        var warnings = DeclaredExpectation.Warnings(outcome);
        Assert.NotNull(warnings);
        Assert.Single(warnings!);
        Assert.Contains("Заявленное ожидание не подтверждено", warnings![0], StringComparison.Ordinal);
        Assert.Contains("80214.601837", warnings[0], StringComparison.Ordinal);
        Assert.Contains("79214.601837", warnings[0], StringComparison.Ordinal);
        Assert.Contains("перечитайте объём", warnings[0], StringComparison.Ordinal);

        // The level is CAPPED: a mismatch never reads as geometry_checked, and a lower level is kept.
        Assert.Equal(VerificationLevel.StructureChecked,
            DeclaredExpectation.CapLevel(VerificationLevel.GeometryChecked, outcome));
        Assert.Equal(VerificationLevel.CallReturned,
            DeclaredExpectation.CapLevel(VerificationLevel.CallReturned, outcome));

        // The named gap carries the same numbers and says the call was NOT rejected.
        var reason = DeclaredExpectation.NotConfirmedReason("объём после операции", outcome);
        Assert.Contains(DeclaredExpectation.NotConfirmedCode, reason, StringComparison.Ordinal);
        Assert.Contains("80214.601837", reason, StringComparison.Ordinal);
        Assert.Contains("НЕ отвергнут", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredExpectation_ThatCouldNotBeCompared_IsUnverifiable_NotARefusal()
    {
        var outcome = DeclaredExpectation.Evaluate(80000d, null);

        Assert.Equal(DeclaredExpectationVerdict.Unverifiable, outcome.Verdict);
        Assert.True(outcome.IsUnverifiable);
        Assert.False(outcome.IsNotConfirmed);
        Assert.Null(DeclaredExpectation.Warnings(outcome));
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
    public void TwoReadsThatAgree_AndBothMissTheExpectation_AreNotConfirmed()
    {
        // Both reads readable and equal, and neither matched: the mismatch is published, not refused.
        var outcome = DeclaredExpectation.Evaluate(80214.60183660255d, 79214.60183660254d,
            () => 79214.60183660254d);

        Assert.Equal(DeclaredExpectationVerdict.NotConfirmed, outcome.Verdict);
        Assert.True(outcome.IsNotConfirmed);
        Assert.Equal(79214.60183660254d, outcome.MeasuredMm3);
    }

    [Fact]
    public void TwoReadsThatDisagree_AreANamedGap_NotARefusal()
    {
        // MEASURED: the model state can read one operation behind. A second read that differs from the
        // first means the measurement is not repeatable, so neither number may be held against the
        // declared expectation — this is a NAMED gap, never a false finding.
        var first = 79123.456789d;
        var second = 80321.987654d;

        var outcome = DeclaredExpectation.Evaluate(80000d, first, () => second);

        Assert.Equal(DeclaredExpectationVerdict.Unverifiable, outcome.Verdict);
        Assert.True(outcome.IsUnverifiable);
        Assert.False(outcome.IsNotConfirmed);
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
        Assert.False(outcome.IsNotConfirmed);
        Assert.Contains("declared_expectation_reads_diverged", outcome.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoUnconfirmedOutcomes_PublishTwoWarnings()
    {
        // A tool with two declared expectations (an edit declares both a delta and a resulting volume)
        // names both, and each warning carries its own numbers.
        var delta = DeclaredExpectation.Evaluate(100d, 0d);
        var volume = DeclaredExpectation.Evaluate(200d, 1539.38d);

        var warnings = DeclaredExpectation.Warnings(delta, volume);

        Assert.NotNull(warnings);
        Assert.Equal(2, warnings!.Count);
        Assert.Contains("заявлено 100", warnings[0], StringComparison.Ordinal);
        Assert.Contains("заявлено 200", warnings[1], StringComparison.Ordinal);
    }
}
