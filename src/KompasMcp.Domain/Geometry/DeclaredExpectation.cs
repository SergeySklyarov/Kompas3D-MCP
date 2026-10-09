using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>How the client's declared expectation of a mutation's outcome relates to what was measured.</summary>
/// <remarks>INVARIANT: an expectation the client did not declare is neither confirmed nor refused; a
/// declared expectation that could not be COMPARED (the measurement was not read) is not a refusal but a
/// named gap, because "we could not check" and "we checked and it did not match" are different facts.
/// History: docs/decisions/adapter-core.md#declared-expectation-rule</remarks>
public enum DeclaredExpectationVerdict
{
    /// <summary>No expectation was declared: the mutation is decided by its own checks.</summary>
    NotDeclared,

    /// <summary>Declared and matched within <see cref="ProfileArea.Tolerance"/>.</summary>
    Confirmed,

    /// <summary>Declared and contradicted by the measurement: a FAILED check, not a refusal.</summary>
    NotConfirmed,

    /// <summary>Declared but not comparable — the measured value could not be read.</summary>
    Unverifiable,
}

/// <summary>The outcome of comparing a declared expectation with a measurement, with the numbers the failed
/// check and the named gap must carry.</summary>
public readonly record struct DeclaredExpectationOutcome(
    DeclaredExpectationVerdict Verdict,
    double? ExpectedMm3,
    double? MeasuredMm3,
    double ToleranceMm3,
    string? Reason = null)
{
    public bool IsDeclared => Verdict != DeclaredExpectationVerdict.NotDeclared;

    public bool IsConfirmed => Verdict == DeclaredExpectationVerdict.Confirmed;

    /// <summary>Declared and contradicted — a failed check, a warning and a lowered level, NOT a refusal.</summary>
    public bool IsNotConfirmed => Verdict == DeclaredExpectationVerdict.NotConfirmed;

    /// <summary>Declared but not comparable — named in <c>unverified_aspects</c>, never a refusal.</summary>
    public bool IsUnverifiable => Verdict == DeclaredExpectationVerdict.Unverifiable;

    /// <summary>Measured minus declared, or null when one of the two is missing.</summary>
    public double? DifferenceMm3 => ExpectedMm3 is double expected && MeasuredMm3 is double measured
        ? measured - expected
        : null;
}

/// <summary>The single rule that turns a client-declared expectation of a mutation's result into a verdict.</summary>
/// <remarks>INVARIANT: a declared expectation that did not hold within the project tolerance does NOT make
/// the call a failure. The call succeeded; the mismatch is published as a failed named check, a warning, a
/// level no higher than <c>structure_checked</c> and a named gap. MEASURED: the model sometimes reads one
/// operation behind, so a refusal here rejected a CORRECT model.
/// INVARIANT (order): the comparison is the geometry check of an operation that HAPPENED, so a "route was
/// not applied" refusal comes first, else the declared mismatch would mask it.
/// History: docs/decisions/adapter-core.md#declared-expectation-rule</remarks>
public static class DeclaredExpectation
{
    /// <summary>The one code the failed check and the named gap carry, whatever the tool.</summary>
    public const string NotConfirmedCode = "declared_expectation_not_confirmed";

    /// <summary>The one tolerance, shared with the profile area: an absolute floor plus a relative part.</summary>
    public static double Tolerance(double expectedMm3) => ProfileArea.Tolerance(expectedMm3);

    /// <summary>Compare a declared expectation with a measurement. A null expectation is "not declared";
    /// a declared expectation with no measurement is "not comparable", not "zero".</summary>
    public static DeclaredExpectationOutcome Evaluate(double? expectedMm3, double? measuredMm3)
    {
        if (expectedMm3 is not double expected)
        {
            return new DeclaredExpectationOutcome(
                DeclaredExpectationVerdict.NotDeclared, null, measuredMm3, 0d);
        }

        var tolerance = Tolerance(expected);
        if (measuredMm3 is not double measured)
        {
            return new DeclaredExpectationOutcome(
                DeclaredExpectationVerdict.Unverifiable, expected, null, tolerance);
        }

        return Math.Abs(measured - expected) <= tolerance
            ? new DeclaredExpectationOutcome(
                DeclaredExpectationVerdict.Confirmed, expected, measured, tolerance)
            : new DeclaredExpectationOutcome(
                DeclaredExpectationVerdict.NotConfirmed, expected, measured, tolerance);
    }

    /// <summary>Compare a declared expectation with a measurement that may be RE-READ once when the first
    /// read did not confirm it; <paramref name="rereadMm3"/> repeats the MEASUREMENT by the same route.</summary>
    /// <remarks>WHY A SECOND READ. MEASURED: the model state sometimes reads one operation behind, so a
    /// single read can disagree while the model is correct. INVARIANT: the mismatch is published only when
    /// BOTH reads are readable and in AGREEMENT; reads that DISAGREE are a NAMED gap, not a mismatch — a
    /// stale read must not become a false finding.
    /// History: docs/decisions/adapter-core.md#declared-expectation-rule</remarks>
    public static DeclaredExpectationOutcome Evaluate(
        double? expectedMm3, double? measuredMm3, Func<double?>? rereadMm3)
    {
        var first = Evaluate(expectedMm3, measuredMm3);
        if (rereadMm3 is null || !first.IsDeclared || first.IsConfirmed)
        {
            return first;
        }

        var second = Evaluate(expectedMm3, rereadMm3());
        if (second.IsConfirmed)
        {
            return second;
        }

        if (first.MeasuredMm3 is double a && second.MeasuredMm3 is double b
            && Math.Abs(a - b) <= Tolerance(first.ExpectedMm3 ?? 0d))
        {
            // Both reads readable and in agreement, and neither matched: a genuine mismatch.
            return first;
        }

        if (first.MeasuredMm3 is null && second.MeasuredMm3 is null)
        {
            // Both reads failed: that is "could not be compared", not "two reads disagreed".
            return first;
        }

        return new DeclaredExpectationOutcome(
            DeclaredExpectationVerdict.Unverifiable,
            first.ExpectedMm3,
            first.MeasuredMm3,
            first.ToleranceMm3,
            DivergentReadsReason(first.MeasuredMm3, second.MeasuredMm3));
    }

    /// <summary>The named gap for a declared expectation that could not be compared.</summary>
    public static string UnreadableReason(string quantity) =>
        "declared_expectation_unreadable — заявленное ожидание (" + quantity + ") не с чем сравнить: "
        + "величина после операции не прочитана. Это НЕ подтверждение и НЕ отказ: сравнивать было нечего.";

    /// <summary>The named gap for two reads of the same quantity that DISAGREE — the measurement is not
    /// repeatable, so neither number can be held against the declared expectation.</summary>
    public static string DivergentReadsReason(double? firstMm3, double? secondMm3) =>
        "declared_expectation_reads_diverged — два чтения объёма разошлись: " + Num(firstMm3)
        + " и " + Num(secondMm3) + ". Расхождение означает, что измерение неповторяемо (устаревшее "
        + "чтение), а не что модель не совпала с заявленным; уровень оставлен не выше structure_checked.";

    /// <summary>The named gap for a declared expectation that did not hold: the same numbers as the
    /// failed check, and the reason the call is still a success.</summary>
    public static string NotConfirmedReason(string quantity, DeclaredExpectationOutcome outcome) =>
        NotConfirmedCode + " — заявленное ожидание (" + quantity + ") не подтверждено: заявлено "
        + Num(outcome.ExpectedMm3) + ", измерено " + Num(outcome.MeasuredMm3) + ", разность "
        + Num(outcome.DifferenceMm3) + ", допуск " + Num(outcome.ToleranceMm3) + ". Вызов НЕ отвергнут: "
        + "модель могла отстать на одну операцию.";

    /// <summary>The warning a caller reads first among the geometry-related ones.</summary>
    public static string NotConfirmedWarning(DeclaredExpectationOutcome outcome) =>
        "Заявленное ожидание не подтверждено: заявлено " + Num(outcome.ExpectedMm3) + ", измерено "
        + Num(outcome.MeasuredMm3) + ", разность " + Num(outcome.DifferenceMm3) + "; модель могла "
        + "отстать на одну операцию - перечитайте объём перед выводом.";

    /// <summary>The warning list a result publishes: empty unless an outcome did not hold. One entry per
    /// unconfirmed outcome, so a tool with two declared expectations names both.</summary>
    public static IReadOnlyList<string>? Warnings(params DeclaredExpectationOutcome[] outcomes)
    {
        var list = new List<string>();
        foreach (var outcome in outcomes)
        {
            if (outcome.IsNotConfirmed)
            {
                list.Add(NotConfirmedWarning(outcome));
            }
        }

        return list.Count == 0 ? null : list;
    }

    /// <summary>The reason to publish for an unverifiable outcome: the divergence text when the two reads
    /// disagreed, otherwise the standard "could not be compared" text.</summary>
    public static string UnverifiableReason(string quantity, DeclaredExpectationOutcome outcome) =>
        outcome.Reason ?? UnreadableReason(quantity);

    /// <summary>Lower a level when the declared expectation did not hold: a mismatch never reads as
    /// <c>geometry_checked</c>. A level already at or below <c>structure_checked</c> is returned as is.</summary>
    public static VerificationLevel CapLevel(VerificationLevel level, DeclaredExpectationOutcome outcome) =>
        outcome.IsNotConfirmed && level > VerificationLevel.StructureChecked
            ? VerificationLevel.StructureChecked
            : level;

    /// <summary>The short check name a tool adds for the comparison, so the response reads the same way
    /// whatever the tool. A mismatch carries the difference and the tolerance in <c>observed</c>.</summary>
    public static NamedCheck Check(string name, DeclaredExpectationOutcome outcome) =>
        new(name,
            outcome.IsConfirmed,
            Observed: outcome.IsNotConfirmed
                ? Num(outcome.MeasuredMm3) + " (разность " + Num(outcome.DifferenceMm3)
                    + ", допуск " + Num(outcome.ToleranceMm3) + ")"
                : Num(outcome.MeasuredMm3),
            Expected: outcome.IsDeclared ? Num(outcome.ExpectedMm3) : "не задано");

    private static string Num(double? value) => value is double number
        ? number.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
        : "не читается";
}
